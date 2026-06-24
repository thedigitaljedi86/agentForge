namespace DevAgent.Worker.DotNet;

using DevAgent.Bridge.Git;
using DevAgent.Contracts.Sandbox;
using DevAgent.Forge;
using DevAgent.Guard.Execution;
using DevAgent.Guard.Paths;

/// <summary>
/// The deterministic .NET-upgrade job: rewrite the target framework of EVERY
/// project in the repository to a newer one (e.g. net6.0 -> net8.0), then
/// build/test/push and open a review-required PR. Framework bumps frequently
/// break the build, so this is a prime case for the optional Forge build-repair
/// step — the agent gets one bounded attempt to fix the compilation, and the
/// worker still re-verifies and opens a review-required PR (never a merge).
///
/// Like the NuGet job, the heavy lifting lives in <see cref="RepoWorkflow"/>;
/// this class only supplies the per-job edit (the target-framework rewrite).
/// </summary>
public sealed class DotNetUpgradeWorker
{
    private readonly RepoWorkflow _workflow;
    private readonly TargetFrameworkUpdater _updater;

    public DotNetUpgradeWorker(
        SafeCommandRunner commandRunner,
        WorkspacePathValidator pathValidator,
        TargetFrameworkUpdater updater,
        IGitProvider gitProvider,
        Func<string, ICodingAgent>? buildRepairAgentFactory = null)
    {
        _workflow = new RepoWorkflow(commandRunner, pathValidator, gitProvider, buildRepairAgentFactory);
        _updater = updater;
    }

    public Task<SandboxJobResult> RunAsync(DotNetUpgradeWorkerSettings settings, CancellationToken cancellationToken = default)
    {
        var request = new RepoWorkflowRequest
        {
            JobId = settings.JobId,
            CloneUrl = settings.CloneUrl,
            BaseBranch = settings.BaseBranch,
            BranchName = $"devagent/dotnet-upgrade-{settings.TargetFramework}",
            CommitMessage = $"Upgrade target framework to {settings.TargetFramework}",
            PullRequestTitle = $"Upgrade all projects to {settings.TargetFramework}",
            PullRequestIntro = "Automated .NET target-framework upgrade by DotNetUpgrader.",
        };

        return _workflow.RunAsync(request, repoPath =>
        {
            var update = _updater.UpdateInDirectory(repoPath, settings.TargetFramework);
            return new RepoMutation(update.Changed, update.Message);
        }, cancellationToken);
    }
}
