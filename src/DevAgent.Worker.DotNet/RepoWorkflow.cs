namespace DevAgent.Worker.DotNet;

using DevAgent.Bridge.Git;
using DevAgent.Contracts.Jobs;
using DevAgent.Contracts.Sandbox;
using DevAgent.Forge;
using DevAgent.Guard.Execution;
using DevAgent.Guard.Paths;

/// <summary>Result of a deterministic workspace mutation (the per-job edit step).</summary>
public sealed record RepoMutation(bool Changed, string Message);

/// <summary>
/// The shared sandbox workflow that every DevAgent job follows:
/// clone -> branch -> (deterministic mutation) -> restore/build/test -> push ->
/// open a review-required pull request. The per-job edit is supplied as a
/// delegate, so NuGet updates and .NET upgrades reuse exactly the same
/// build/test/repair/push/PR machinery.
///
/// OPT-IN BUILD REPAIR: when an <see cref="ICodingAgent"/> factory is supplied
/// and the post-mutation build fails, the controlled Forge agent gets ONE bounded
/// attempt to fix the build through its structured tools (no shell). The worker
/// then re-runs build/test deterministically and still opens a review-required
/// PR — the agent never merges and never pushes; that stays the worker's job.
/// </summary>
public sealed class RepoWorkflow
{
    private readonly SafeCommandRunner _commandRunner;
    private readonly WorkspacePathValidator _pathValidator;
    private readonly IGitProvider _gitProvider;
    private readonly Func<string, ICodingAgent>? _buildRepairAgentFactory;
    private readonly string? _skillInstructions;

    public RepoWorkflow(
        SafeCommandRunner commandRunner,
        WorkspacePathValidator pathValidator,
        IGitProvider gitProvider,
        Func<string, ICodingAgent>? buildRepairAgentFactory = null,
        string? skillInstructions = null)
    {
        _commandRunner = commandRunner;
        _pathValidator = pathValidator;
        _gitProvider = gitProvider;
        _buildRepairAgentFactory = buildRepairAgentFactory;
        _skillInstructions = skillInstructions;
    }

    public async Task<SandboxJobResult> RunAsync(
        RepoWorkflowRequest request,
        Func<string, RepoMutation> mutate,
        CancellationToken cancellationToken = default)
    {
        const string repoDir = "repo"; // workspace-relative checkout directory

        // 1. Clone into the workspace (git, allowlisted).
        var clone = await _commandRunner.RunAsync(
            "git", new[] { "clone", "--branch", request.BaseBranch, request.CloneUrl, repoDir },
            workingSubPath: "", cancellationToken);
        if (!clone.Succeeded)
        {
            return Failed(request.JobId, $"Clone failed: {clone.StandardError}");
        }

        // 2. Create a work branch.
        var checkout = await _commandRunner.RunAsync(
            "git", new[] { "checkout", "-b", request.BranchName }, repoDir, cancellationToken);
        if (!checkout.Succeeded)
        {
            return Failed(request.JobId, $"Branch creation failed: {checkout.StandardError}");
        }

        // 3. Apply the deterministic per-job edit (no shell, no LLM).
        var repoPath = _pathValidator.ResolveInsideWorkspace(repoDir);
        var mutation = mutate(repoPath);
        if (!mutation.Changed)
        {
            return new SandboxJobResult
            {
                JobId = request.JobId,
                Status = AgentJobStatus.NoChange,
                Message = mutation.Message,
            };
        }

        // 4. restore -> build -> (optional repair) -> build -> test.
        var (buildOk, testsOk, repairSummary) = await BuildTestRepairAsync(repoDir, repoPath, request.JobId, cancellationToken);

        // If the build is still broken, stop here and open NO pull request.
        if (!buildOk)
        {
            return new SandboxJobResult
            {
                JobId = request.JobId,
                Status = AgentJobStatus.Failed,
                BranchName = request.BranchName,
                BuildSucceeded = false,
                Message = repairSummary is null
                    ? "Build failed after the change."
                    : $"Build still failed after an LLM repair attempt. {repairSummary}",
            };
        }

        // 5. Commit and push the branch.
        await _commandRunner.RunAsync("git", new[] { "add", "-A" }, repoDir, cancellationToken);
        await _commandRunner.RunAsync("git", new[] { "commit", "-m", request.CommitMessage }, repoDir, cancellationToken);
        var push = await _commandRunner.RunAsync(
            "git", new[] { "push", "--set-upstream", "origin", request.BranchName }, repoDir, cancellationToken);
        if (!push.Succeeded)
        {
            return Failed(request.JobId, $"Push failed: {push.StandardError}");
        }

        // 6. Open a pull request (never auto-merge).
        var repository = await _gitProvider.GetRepositoryAsync(request.CloneUrl, cancellationToken);
        var body = request.PullRequestIntro
                   + $"\n\nBuild succeeded. Tests succeeded: {testsOk}."
                   + (repairSummary is null ? string.Empty : $"\n\nBuild was repaired by the Forge coding agent: {repairSummary}")
                   + "\n\nThis PR requires human review; auto-merge is disabled.";
        var pr = await _gitProvider.CreatePullRequestAsync(repository, new PullRequestRequest
        {
            SourceBranch = request.BranchName,
            TargetBranch = request.BaseBranch,
            Title = request.PullRequestTitle,
            Body = body,
            AutoMerge = false,
        }, cancellationToken);

        return new SandboxJobResult
        {
            JobId = request.JobId,
            Status = testsOk ? AgentJobStatus.Succeeded : AgentJobStatus.Failed,
            BranchName = request.BranchName,
            BuildSucceeded = true,
            TestsSucceeded = testsOk,
            PullRequestUrl = pr.Url,
            Message = testsOk
                ? (repairSummary is null ? "Pull request created." : "Build repaired by the coding agent; pull request created.")
                : "Build passed but tests failed; PR created for review.",
        };
    }

    /// <summary>
    /// PipelineFix flow: clone the FAILING branch, reproduce locally, let the
    /// (required) coding agent repair with the CI log as context, re-verify
    /// deterministically, and open a PR only when there are REAL changes and
    /// the build is green. Green-on-arrival ⇒ NoChange (nothing to fix here).
    /// </summary>
    public async Task<SandboxJobResult> RunRepairAsync(
        RepoWorkflowRequest request,
        string ciFailureContext,
        CancellationToken cancellationToken = default)
    {
        const string repoDir = "repo";

        var setup = await CloneAndBranchAsync(request, repoDir, cancellationToken);
        if (setup is not null)
        {
            return setup;
        }

        var repoPath = _pathValidator.ResolveInsideWorkspace(repoDir);

        // Reproduce: restore/build, then tests when the build is green.
        var (restore, build) = await RestoreBuildAsync(repoDir, cancellationToken);
        var buildOk = restore.Succeeded && build.Succeeded;
        var testsOk = false;
        CommandResult? test = null;
        if (buildOk)
        {
            test = await _commandRunner.RunAsync("dotnet", new[] { "test", "--no-build" }, repoDir, cancellationToken);
            testsOk = test.Succeeded;
        }

        if (buildOk && testsOk)
        {
            return new SandboxJobResult
            {
                JobId = request.JobId,
                Status = AgentJobStatus.NoChange,
                Message = "Could not reproduce the CI failure locally — build and tests are green on this branch.",
            };
        }

        // A pipeline fix without an LLM has nothing to work with: fail safely.
        if (_buildRepairAgentFactory is null)
        {
            return Failed(request.JobId,
                "The CI failure reproduces locally, but no LLM is configured for this agent — cannot attempt a repair.");
        }

        var agent = _buildRepairAgentFactory(repoPath);
        var repair = await agent.RunAsync(new CodingAgentTask
        {
            JobId = request.JobId,
            Goal = "Fix the cause of the failing CI pipeline. Make the smallest change that makes the build and tests pass. " +
                   "Do not change behaviour beyond what the failure requires.",
            WorkspaceRoot = repoPath,
            FailureContext =
                $"CI failure log (tail):\n{ciFailureContext}\n\n" +
                $"Local restore stderr:\n{restore.StandardError}\n\nLocal build stderr:\n{build.StandardError}" +
                (test is null ? string.Empty : $"\n\nLocal test output:\n{Tail(test.StandardOutput + test.StandardError)}"),
            SkillInstructions = _skillInstructions,
        }, cancellationToken);

        // Deterministic re-verification — the agent's claim is never trusted.
        (restore, build) = await RestoreBuildAsync(repoDir, cancellationToken);
        buildOk = restore.Succeeded && build.Succeeded;
        if (buildOk)
        {
            test = await _commandRunner.RunAsync("dotnet", new[] { "test", "--no-build" }, repoDir, cancellationToken);
            testsOk = test.Succeeded;
        }

        if (!buildOk || !testsOk)
        {
            return new SandboxJobResult
            {
                JobId = request.JobId,
                Status = AgentJobStatus.Failed,
                BranchName = request.BranchName,
                BuildSucceeded = buildOk,
                TestsSucceeded = testsOk,
                Message = $"The repair attempt did not make the pipeline green (build={buildOk}, tests={testsOk}). " +
                          $"{repair.ReasoningSummary}",
            };
        }

        if (!await HasWorkingTreeChangesAsync(repoDir, cancellationToken))
        {
            return new SandboxJobResult
            {
                JobId = request.JobId,
                Status = AgentJobStatus.NoChange,
                Message = "The build became green without any file changes — nothing to propose.",
            };
        }

        return await CommitPushOpenPrAsync(request, repoDir, testsOk: true,
            repairSummary: repair.ReasoningSummary, cancellationToken);
    }

    /// <summary>
    /// DocUpdate flow: a deterministic documentation step first (no LLM), then
    /// an optional write-scoped authoring agent, then a full build/test
    /// re-verification (documentation must never break the build), and a PR
    /// only when files actually changed.
    /// </summary>
    public async Task<SandboxJobResult> RunAuthoringAsync(
        RepoWorkflowRequest request,
        Func<string, RepoMutation> deterministicStep,
        string agentGoal,
        CancellationToken cancellationToken = default)
    {
        const string repoDir = "repo";

        var setup = await CloneAndBranchAsync(request, repoDir, cancellationToken);
        if (setup is not null)
        {
            return setup;
        }

        var repoPath = _pathValidator.ResolveInsideWorkspace(repoDir);
        var deterministic = deterministicStep(repoPath);

        string? authoringSummary = null;
        if (_buildRepairAgentFactory is not null)
        {
            var agent = _buildRepairAgentFactory(repoPath);
            var result = await agent.RunAsync(new CodingAgentTask
            {
                JobId = request.JobId,
                Goal = agentGoal,
                WorkspaceRoot = repoPath,
                SkillInstructions = _skillInstructions,
            }, cancellationToken);
            authoringSummary = result.ReasoningSummary;
        }

        // Docs must never break the build; verify deterministically either way.
        var (restore, build) = await RestoreBuildAsync(repoDir, cancellationToken);
        if (!restore.Succeeded || !build.Succeeded)
        {
            return Failed(request.JobId, "The build is no longer green after the documentation update — refusing to open a PR.");
        }

        var testRun = await _commandRunner.RunAsync("dotnet", new[] { "test", "--no-build" }, repoDir, cancellationToken);

        if (!await HasWorkingTreeChangesAsync(repoDir, cancellationToken))
        {
            return new SandboxJobResult
            {
                JobId = request.JobId,
                Status = AgentJobStatus.NoChange,
                Message = $"Documentation is already up to date. {deterministic.Message}",
            };
        }

        return await CommitPushOpenPrAsync(request, repoDir, testRun.Succeeded, authoringSummary, cancellationToken);
    }

    // ---- shared building blocks ----

    /// <summary>Clone + create the work branch; null when everything succeeded.</summary>
    private async Task<SandboxJobResult?> CloneAndBranchAsync(
        RepoWorkflowRequest request, string repoDir, CancellationToken cancellationToken)
    {
        var clone = await _commandRunner.RunAsync(
            "git", new[] { "clone", "--branch", request.BaseBranch, request.CloneUrl, repoDir },
            workingSubPath: "", cancellationToken);
        if (!clone.Succeeded)
        {
            return Failed(request.JobId, $"Clone failed: {clone.StandardError}");
        }

        var checkout = await _commandRunner.RunAsync(
            "git", new[] { "checkout", "-b", request.BranchName }, repoDir, cancellationToken);
        return checkout.Succeeded ? null : Failed(request.JobId, $"Branch creation failed: {checkout.StandardError}");
    }

    private async Task<(CommandResult Restore, CommandResult Build)> RestoreBuildAsync(
        string repoDir, CancellationToken cancellationToken)
    {
        var restore = await _commandRunner.RunAsync("dotnet", new[] { "restore" }, repoDir, cancellationToken);
        var build = await _commandRunner.RunAsync("dotnet", new[] { "build", "--no-restore" }, repoDir, cancellationToken);
        return (restore, build);
    }

    private async Task<bool> HasWorkingTreeChangesAsync(string repoDir, CancellationToken cancellationToken)
    {
        var status = await _commandRunner.RunAsync("git", new[] { "status", "--porcelain" }, repoDir, cancellationToken);
        return status.Succeeded && !string.IsNullOrWhiteSpace(status.StandardOutput);
    }

    private async Task<SandboxJobResult> CommitPushOpenPrAsync(
        RepoWorkflowRequest request, string repoDir, bool testsOk, string? repairSummary, CancellationToken cancellationToken)
    {
        await _commandRunner.RunAsync("git", new[] { "add", "-A" }, repoDir, cancellationToken);
        await _commandRunner.RunAsync("git", new[] { "commit", "-m", request.CommitMessage }, repoDir, cancellationToken);
        var push = await _commandRunner.RunAsync(
            "git", new[] { "push", "--set-upstream", "origin", request.BranchName }, repoDir, cancellationToken);
        if (!push.Succeeded)
        {
            return Failed(request.JobId, $"Push failed: {push.StandardError}");
        }

        var repository = await _gitProvider.GetRepositoryAsync(request.CloneUrl, cancellationToken);
        var body = request.PullRequestIntro
                   + $"\n\nBuild succeeded. Tests succeeded: {testsOk}."
                   + (repairSummary is null ? string.Empty : $"\n\nAgent summary: {repairSummary}")
                   + "\n\nThis PR requires human review; auto-merge is disabled.";
        var pr = await _gitProvider.CreatePullRequestAsync(repository, new PullRequestRequest
        {
            SourceBranch = request.BranchName,
            TargetBranch = request.BaseBranch,
            Title = request.PullRequestTitle,
            Body = body,
            AutoMerge = false,
        }, cancellationToken);

        return new SandboxJobResult
        {
            JobId = request.JobId,
            Status = testsOk ? AgentJobStatus.Succeeded : AgentJobStatus.Failed,
            BranchName = request.BranchName,
            BuildSucceeded = true,
            TestsSucceeded = testsOk,
            PullRequestUrl = pr.Url,
            Message = testsOk ? "Pull request created." : "Build passed but tests failed; PR created for review.",
        };
    }

    private static string Tail(string text, int max = 6000) =>
        text.Length <= max ? text : "…(truncated)\n" + text[^max..];

    private async Task<(bool BuildOk, bool TestsOk, string? RepairSummary)> BuildTestRepairAsync(
        string repoDir, string repoPath, string jobId, CancellationToken cancellationToken)
    {
        var restore = await _commandRunner.RunAsync("dotnet", new[] { "restore" }, repoDir, cancellationToken);
        var build = await _commandRunner.RunAsync("dotnet", new[] { "build", "--no-restore" }, repoDir, cancellationToken);
        var buildOk = restore.Succeeded && build.Succeeded;
        string? repairSummary = null;

        // OPT-IN: hand a broken build to the controlled coding agent once.
        if (!buildOk && _buildRepairAgentFactory is not null)
        {
            var agent = _buildRepairAgentFactory(repoPath);
            var task = new CodingAgentTask
            {
                JobId = jobId,
                Goal = "Fix the failing build caused by the dependency/framework change. Do not change behaviour beyond what is needed to compile and pass tests.",
                WorkspaceRoot = repoPath,
                FailureContext = $"restore stderr:\n{restore.StandardError}\n\nbuild stderr:\n{build.StandardError}",
                SkillInstructions = _skillInstructions,
            };

            var repair = await agent.RunAsync(task, cancellationToken);
            repairSummary = repair.ReasoningSummary;

            // Re-verify deterministically; the agent's edits are not trusted blindly.
            restore = await _commandRunner.RunAsync("dotnet", new[] { "restore" }, repoDir, cancellationToken);
            build = await _commandRunner.RunAsync("dotnet", new[] { "build", "--no-restore" }, repoDir, cancellationToken);
            buildOk = restore.Succeeded && build.Succeeded;
        }

        var testsOk = false;
        if (buildOk)
        {
            var test = await _commandRunner.RunAsync("dotnet", new[] { "test", "--no-build" }, repoDir, cancellationToken);
            testsOk = test.Succeeded;
        }

        return (buildOk, testsOk, repairSummary);
    }

    private static SandboxJobResult Failed(string jobId, string message) => new()
    {
        JobId = jobId,
        Status = AgentJobStatus.Failed,
        Message = message,
    };
}

/// <summary>The repo-independent inputs to a single <see cref="RepoWorkflow"/> run.</summary>
public sealed record RepoWorkflowRequest
{
    public required string JobId { get; init; }
    public required string CloneUrl { get; init; }
    public required string BaseBranch { get; init; }
    public required string BranchName { get; init; }
    public required string CommitMessage { get; init; }
    public required string PullRequestTitle { get; init; }
    public required string PullRequestIntro { get; init; }
}
