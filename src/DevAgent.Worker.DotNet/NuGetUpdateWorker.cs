namespace DevAgent.Worker.DotNet;

using DevAgent.Bridge.Git;
using DevAgent.Contracts.Jobs;
using DevAgent.Contracts.Sandbox;
using DevAgent.Guard.Execution;
using DevAgent.Guard.Paths;
using DevAgent.Guard.Policies;

/// <summary>
/// Orchestrates the deterministic NuGet-update workflow inside the sandbox:
/// clone -> branch -> update PackageReference -> restore -> build -> test ->
/// push -> open PR. Every external command runs through the SafeCommandRunner,
/// and every path stays inside the workspace.
///
/// SECURITY: There is no LLM here and no shell. Commands are limited to git and
/// dotnet by <see cref="CommandPolicy"/>. The result is always a pull request,
/// never a merge.
/// </summary>
public sealed class NuGetUpdateWorker
{
    private readonly SafeCommandRunner _commandRunner;
    private readonly WorkspacePathValidator _pathValidator;
    private readonly PackageReferenceUpdater _updater;
    private readonly IGitProvider _gitProvider;

    public NuGetUpdateWorker(
        SafeCommandRunner commandRunner,
        WorkspacePathValidator pathValidator,
        PackageReferenceUpdater updater,
        IGitProvider gitProvider)
    {
        _commandRunner = commandRunner;
        _pathValidator = pathValidator;
        _updater = updater;
        _gitProvider = gitProvider;
    }

    public async Task<SandboxJobResult> RunAsync(WorkerJobSettings settings, CancellationToken cancellationToken = default)
    {
        var branchName = $"devagent/nuget-{Sanitize(settings.PackageId)}-{settings.TargetVersion}";
        const string repoDir = "repo"; // workspace-relative checkout directory

        // 1. Clone into the workspace (git, allowlisted).
        var clone = await _commandRunner.RunAsync(
            "git", new[] { "clone", "--branch", settings.BaseBranch, settings.CloneUrl, repoDir },
            workingSubPath: "", cancellationToken);
        if (!clone.Succeeded)
        {
            return Failed(settings.JobId, $"Clone failed: {clone.StandardError}");
        }

        // 2. Create a work branch.
        var checkout = await _commandRunner.RunAsync(
            "git", new[] { "checkout", "-b", branchName }, workingSubPath: repoDir, cancellationToken);
        if (!checkout.Succeeded)
        {
            return Failed(settings.JobId, $"Branch creation failed: {checkout.StandardError}");
        }

        // 3. Update the PackageReference deterministically (no shell, no LLM).
        var repoPath = _pathValidator.ResolveInsideWorkspace(repoDir);
        var update = _updater.UpdateInDirectory(repoPath, settings.PackageId, settings.TargetVersion);
        if (!update.Changed)
        {
            return new SandboxJobResult
            {
                JobId = settings.JobId,
                Status = AgentJobStatus.NoChange,
                Message = update.Message,
            };
        }

        // 4. restore -> build -> test (all via dotnet, allowlisted).
        var restore = await _commandRunner.RunAsync("dotnet", new[] { "restore" }, repoDir, cancellationToken);
        var build = await _commandRunner.RunAsync("dotnet", new[] { "build", "--no-restore" }, repoDir, cancellationToken);
        var buildOk = restore.Succeeded && build.Succeeded;

        CommandResult? test = null;
        if (buildOk)
        {
            test = await _commandRunner.RunAsync("dotnet", new[] { "test", "--no-build" }, repoDir, cancellationToken);
        }

        var testsOk = test?.Succeeded ?? false;

        // If the build broke, stop here. A future Forge LLM step could attempt a
        // controlled fix; for now we fail safely without opening a PR.
        if (!buildOk)
        {
            return new SandboxJobResult
            {
                JobId = settings.JobId,
                Status = AgentJobStatus.Failed,
                BranchName = branchName,
                BuildSucceeded = false,
                Message = $"Build failed after update. restore={restore.Succeeded} build={build.Succeeded}",
            };
        }

        // 5. Commit and push the branch.
        await _commandRunner.RunAsync("git", new[] { "add", "-A" }, repoDir, cancellationToken);
        await _commandRunner.RunAsync(
            "git", new[] { "commit", "-m", $"Update {settings.PackageId} to {settings.TargetVersion}" },
            repoDir, cancellationToken);
        var push = await _commandRunner.RunAsync(
            "git", new[] { "push", "--set-upstream", "origin", branchName }, repoDir, cancellationToken);
        if (!push.Succeeded)
        {
            return Failed(settings.JobId, $"Push failed: {push.StandardError}");
        }

        // 6. Open a pull request (never auto-merge).
        var repository = await _gitProvider.GetRepositoryAsync(settings.CloneUrl, cancellationToken);
        var pr = await _gitProvider.CreatePullRequestAsync(repository, new PullRequestRequest
        {
            SourceBranch = branchName,
            TargetBranch = settings.BaseBranch,
            Title = $"Update {settings.PackageId} to {settings.TargetVersion}",
            Body = $"Automated dependency update by DependencyPilot.\n\nBuild succeeded. Tests succeeded: {testsOk}.\n\n" +
                   "This PR requires human review; auto-merge is disabled.",
            AutoMerge = false,
        }, cancellationToken);

        return new SandboxJobResult
        {
            JobId = settings.JobId,
            Status = testsOk ? AgentJobStatus.Succeeded : AgentJobStatus.Failed,
            BranchName = branchName,
            BuildSucceeded = true,
            TestsSucceeded = testsOk,
            PullRequestUrl = pr.Url,
            Message = testsOk ? "Pull request created." : "Build passed but tests failed; PR created for review.",
        };
    }

    private static SandboxJobResult Failed(string jobId, string message) => new()
    {
        JobId = jobId,
        Status = AgentJobStatus.Failed,
        Message = message,
    };

    private static string Sanitize(string value) =>
        new string(value.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-').ToArray());
}
