namespace DevAgent.Worker.DotNet;

using DevAgent.Bridge.Git;
using DevAgent.Contracts.Sandbox;
using DevAgent.Forge;
using DevAgent.Guard.Execution;
using DevAgent.Guard.Paths;

/// <summary>
/// PipelineDoctor's in-sandbox worker: clone the failing branch, reproduce the
/// CI failure locally, let the caged Forge agent repair it with the CI log as
/// context, re-verify deterministically and open a review-required PR only
/// when there are real, green changes. All heavy lifting lives in
/// <see cref="RepoWorkflow.RunRepairAsync"/>.
/// </summary>
public sealed class PipelineFixWorker
{
    private readonly RepoWorkflow _workflow;

    public PipelineFixWorker(
        SafeCommandRunner commandRunner,
        WorkspacePathValidator pathValidator,
        IGitProvider gitProvider,
        Func<string, ICodingAgent>? repairAgentFactory = null)
    {
        _workflow = new RepoWorkflow(commandRunner, pathValidator, gitProvider, repairAgentFactory,
            Environment.GetEnvironmentVariable("DEVAGENT_SKILL_INSTRUCTIONS"));
    }

    public Task<SandboxJobResult> RunAsync(PipelineFixWorkerSettings settings, CancellationToken cancellationToken = default)
    {
        var request = new RepoWorkflowRequest
        {
            JobId = settings.JobId,
            CloneUrl = settings.CloneUrl,
            BaseBranch = settings.BaseBranch,
            BranchName = $"devagent/pipeline-fix-{settings.JobId}",
            CommitMessage = $"Fix failing CI pipeline on {settings.BaseBranch}",
            PullRequestTitle = $"Fix failing CI pipeline on {settings.BaseBranch}",
            PullRequestIntro = "Automated pipeline repair by PipelineDoctor.",
        };

        return _workflow.RunRepairAsync(request, settings.FailureContext, cancellationToken);
    }
}
