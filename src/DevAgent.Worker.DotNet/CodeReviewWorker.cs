namespace DevAgent.Worker.DotNet;

using DevAgent.Bridge.Git;
using DevAgent.Contracts.Jobs;
using DevAgent.Contracts.Sandbox;
using DevAgent.Forge;
using DevAgent.Guard.Execution;
using DevAgent.Guard.Paths;

/// <summary>
/// CodeReviewer's in-sandbox worker. Deliberately NOT built on
/// <see cref="RepoWorkflow"/>: this flow must never commit, push or open a PR.
///
/// Flow: clone the PR's TARGET branch → fetch the source branch → capture
/// <c>git diff base...source</c> as context → the READ-ONLY Forge agent (its
/// WriteScopePolicy denies every write; it may still read files and run
/// build/test) produces a review → the review is posted as a PR comment via
/// <see cref="IGitProvider.PostPullRequestCommentAsync"/>.
///
/// SECURITY: The only output channel is a comment. The diff content is
/// untrusted PR data — it is context for the agent, never instructions with
/// authority, and the write-denial is enforced by policy, not by prompt.
/// </summary>
public sealed class CodeReviewWorker
{
    private const string RepoDir = "repo";
    private const int MaxDiffChars = 60_000;

    private readonly SafeCommandRunner _commandRunner;
    private readonly WorkspacePathValidator _pathValidator;
    private readonly IGitProvider _gitProvider;
    private readonly Func<string, ICodingAgent>? _reviewAgentFactory;
    private readonly string? _skillInstructions;

    public CodeReviewWorker(
        SafeCommandRunner commandRunner,
        WorkspacePathValidator pathValidator,
        IGitProvider gitProvider,
        Func<string, ICodingAgent>? reviewAgentFactory = null)
    {
        _commandRunner = commandRunner;
        _pathValidator = pathValidator;
        _gitProvider = gitProvider;
        _reviewAgentFactory = reviewAgentFactory;
        _skillInstructions = Environment.GetEnvironmentVariable("DEVAGENT_SKILL_INSTRUCTIONS");
    }

    public async Task<SandboxJobResult> RunAsync(CodeReviewWorkerSettings settings, CancellationToken cancellationToken = default)
    {
        // A review without an LLM has nothing to say — fail safely up front.
        if (_reviewAgentFactory is null)
        {
            return Failed(settings.JobId, "No LLM is configured for the review agent — cannot produce a review.");
        }

        var clone = await _commandRunner.RunAsync(
            "git", new[] { "clone", "--branch", settings.BaseBranch, settings.CloneUrl, RepoDir },
            workingSubPath: "", cancellationToken);
        if (!clone.Succeeded)
        {
            return Failed(settings.JobId, $"Clone failed: {clone.StandardError}");
        }

        var fetch = await _commandRunner.RunAsync(
            "git", new[] { "fetch", "origin", settings.SourceBranch }, RepoDir, cancellationToken);
        if (!fetch.Succeeded)
        {
            return Failed(settings.JobId, $"Fetching source branch '{settings.SourceBranch}' failed: {fetch.StandardError}");
        }

        var diff = await _commandRunner.RunAsync(
            "git", new[] { "diff", $"{settings.BaseBranch}...FETCH_HEAD" }, RepoDir, cancellationToken);
        if (!diff.Succeeded)
        {
            return Failed(settings.JobId, $"Computing the PR diff failed: {diff.StandardError}");
        }

        if (string.IsNullOrWhiteSpace(diff.StandardOutput))
        {
            return new SandboxJobResult
            {
                JobId = settings.JobId,
                Status = AgentJobStatus.NoChange,
                Message = $"'{settings.SourceBranch}' introduces no changes against '{settings.BaseBranch}' — nothing to review.",
            };
        }

        // Review the SOURCE branch state so the agent can read the changed files.
        var checkout = await _commandRunner.RunAsync(
            "git", new[] { "checkout", "FETCH_HEAD" }, RepoDir, cancellationToken);
        if (!checkout.Succeeded)
        {
            return Failed(settings.JobId, $"Checking out the source branch failed: {checkout.StandardError}");
        }

        var repoPath = _pathValidator.ResolveInsideWorkspace(RepoDir);
        var agent = _reviewAgentFactory(repoPath);
        var review = await agent.RunAsync(new CodingAgentTask
        {
            JobId = settings.JobId,
            Goal = "Review the pull request changes shown in the diff below. Read the surrounding code where needed and " +
                   "optionally run the build/tests to check the change. Produce a concise, constructive code review as your " +
                   "final summary: correctness risks, security concerns, missing tests, and clarity issues — with file " +
                   "references. You cannot and must not modify any file; your review comment is your only output.",
            WorkspaceRoot = repoPath,
            FailureContext = $"PR diff ({settings.SourceBranch} -> {settings.BaseBranch}):\n{Truncate(diff.StandardOutput)}",
            SkillInstructions = _skillInstructions,
        }, cancellationToken);

        var reviewText = string.IsNullOrWhiteSpace(review.ReasoningSummary)
            ? "The review agent completed without producing a summary."
            : review.ReasoningSummary!;

        var comment = $"## DevAgent CodeReviewer\n\n{reviewText}\n\n" +
                      "_Automated review by a read-only agent; a human decision is still required._";

        string message;
        if (settings.PrNumber is int prNumber)
        {
            var repository = await _gitProvider.GetRepositoryAsync(settings.CloneUrl, cancellationToken);
            var posted = await _gitProvider.PostPullRequestCommentAsync(repository, prNumber, comment, cancellationToken);
            message = posted.Created
                ? $"Review posted on PR #{prNumber}."
                : $"Review produced, but posting the comment failed: {posted.Message}";
        }
        else
        {
            // No PR number: the review is still recorded in the job result/audit.
            message = $"Review produced (no PR number supplied):\n{reviewText}";
        }

        return new SandboxJobResult
        {
            JobId = settings.JobId,
            Status = AgentJobStatus.Succeeded,
            Message = message,
        };
    }

    private static string Truncate(string text) =>
        text.Length <= MaxDiffChars ? text : text[..MaxDiffChars] + "\n…(diff truncated)";

    private static SandboxJobResult Failed(string jobId, string message) => new()
    {
        JobId = jobId,
        Status = AgentJobStatus.Failed,
        Message = message,
    };
}
