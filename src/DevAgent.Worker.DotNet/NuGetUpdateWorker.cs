namespace DevAgent.Worker.DotNet;

using DevAgent.Bridge.Git;
using DevAgent.Contracts.Sandbox;
using DevAgent.Forge;
using DevAgent.Guard.Execution;
using DevAgent.Guard.Paths;

/// <summary>
/// The deterministic NuGet-update job: bump a single PackageReference across the
/// repository, then build/test/push and open a review-required PR. The actual
/// clone/build/test/push/PR machinery lives in <see cref="RepoWorkflow"/>; this
/// class only supplies the per-job edit (the PackageReference bump) and naming.
///
/// SECURITY: There is no shell here. The deterministic edit involves no model;
/// the optional Forge build-repair step (passed to the workflow) acts only
/// through structured, policy-checked tools and never merges.
/// </summary>
public sealed class NuGetUpdateWorker
{
    private readonly RepoWorkflow _workflow;
    private readonly PackageReferenceUpdater _updater;

    public NuGetUpdateWorker(
        SafeCommandRunner commandRunner,
        WorkspacePathValidator pathValidator,
        PackageReferenceUpdater updater,
        IGitProvider gitProvider,
        Func<string, ICodingAgent>? buildRepairAgentFactory = null)
    {
        _workflow = new RepoWorkflow(commandRunner, pathValidator, gitProvider, buildRepairAgentFactory);
        _updater = updater;
    }

    public Task<SandboxJobResult> RunAsync(WorkerJobSettings settings, CancellationToken cancellationToken = default)
    {
        var request = new RepoWorkflowRequest
        {
            JobId = settings.JobId,
            CloneUrl = settings.CloneUrl,
            BaseBranch = settings.BaseBranch,
            BranchName = $"devagent/nuget-{Sanitize(settings.PackageId)}-{settings.TargetVersion}",
            CommitMessage = $"Update {settings.PackageId} to {settings.TargetVersion}",
            PullRequestTitle = $"Update {settings.PackageId} to {settings.TargetVersion}",
            PullRequestIntro = "Automated dependency update by DependencyPilot.",
        };

        return _workflow.RunAsync(request, repoPath =>
        {
            var update = _updater.UpdateInDirectory(repoPath, settings.PackageId, settings.TargetVersion);
            return new RepoMutation(update.Changed, update.Message);
        }, cancellationToken);
    }

    private static string Sanitize(string value) =>
        new(value.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-').ToArray());
}
