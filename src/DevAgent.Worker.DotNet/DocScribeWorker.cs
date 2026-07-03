namespace DevAgent.Worker.DotNet;

using DevAgent.Bridge.Git;
using DevAgent.Contracts.Sandbox;
using DevAgent.Forge;
using DevAgent.Guard.Execution;
using DevAgent.Guard.Paths;

/// <summary>
/// DocScribe's in-sandbox worker. Two layers:
///   1. DETERMINISTIC: <see cref="DocInventory"/> regenerates docs/CODEMAP.md
///      from the project structure — always runs, needs no LLM.
///   2. OPTIONAL agent authoring: the Forge agent may improve docs/ and
///      README.md — and, thanks to its docs-only <c>WriteScopePolicy</c>
///      (applied by the composition root), it is STRUCTURALLY unable to modify
///      code no matter what its prompt or any injected content says.
/// The build is re-verified afterwards and the result is always a
/// review-required PR, so scheduled runs keep documentation maintained without
/// ever bypassing human review.
/// </summary>
public sealed class DocScribeWorker
{
    private readonly RepoWorkflow _workflow;
    private readonly DocInventory _inventory = new();

    public DocScribeWorker(
        SafeCommandRunner commandRunner,
        WorkspacePathValidator pathValidator,
        IGitProvider gitProvider,
        Func<string, ICodingAgent>? authoringAgentFactory = null)
    {
        _workflow = new RepoWorkflow(commandRunner, pathValidator, gitProvider, authoringAgentFactory,
            Environment.GetEnvironmentVariable("DEVAGENT_SKILL_INSTRUCTIONS"));
    }

    public Task<SandboxJobResult> RunAsync(DocUpdateWorkerSettings settings, CancellationToken cancellationToken = default)
    {
        var request = new RepoWorkflowRequest
        {
            JobId = settings.JobId,
            CloneUrl = settings.CloneUrl,
            BaseBranch = settings.BaseBranch,
            BranchName = $"devagent/docs-{settings.JobId}",
            CommitMessage = "Update repository documentation",
            PullRequestTitle = "Update repository documentation",
            PullRequestIntro = "Automated documentation maintenance by DocScribe.",
        };

        const string goal =
            "Maintain this repository's documentation. docs/CODEMAP.md has just been regenerated mechanically — " +
            "do not edit it. Review the code and update docs/ARCHITECTURE.md (create it if missing) and README.md " +
            "so they accurately describe the current projects, their responsibilities and how they fit together. " +
            "Correct anything that is stale, keep the existing tone and structure, and do not invent features. " +
            "You may only write under docs/ and README.md.";

        return _workflow.RunAuthoringAsync(request, _inventory.GenerateCodeMap, goal, cancellationToken);
    }
}
