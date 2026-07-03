namespace Agents.CodeReviewer;

using DevAgent.Contracts.Jobs;
using Microsoft.Extensions.Options;

/// <summary>
/// The CodeReviewer agent: turns "a PR was opened" events into CodeReview
/// jobs. The in-sandbox review agent is read-only by policy; its only output
/// is a PR comment — a human decision is always still required.
///
/// SECURITY: The webhook payload (repository key, source branch, PR number)
/// is external input. The repository must be on THIS agent's watch list, and
/// the Runner re-validates the key and the branch ref-name afterwards.
/// </summary>
public sealed class CodeReviewerService
{
    private readonly CodeReviewerOptions _options;
    private readonly ICodeReviewTrigger _trigger;

    public CodeReviewerService(IOptions<CodeReviewerOptions> options, ICodeReviewTrigger trigger)
    {
        _options = options.Value;
        _trigger = trigger;
    }

    /// <summary>Handle a PR-opened event (webhook or manual trigger).</summary>
    public async Task<AgentJobResult> HandlePullRequestOpenedAsync(
        string repositoryKey,
        string sourceBranch,
        int? prNumber,
        CancellationToken cancellationToken = default)
    {
        if (!_options.RepositoryKeys.Contains(repositoryKey, StringComparer.OrdinalIgnoreCase))
        {
            return AgentJobResult.Rejected(
                Guid.NewGuid().ToString("N"),
                $"Repository '{repositoryKey}' is not watched by CodeReviewer.");
        }

        return await _trigger.StartCodeReviewAsync(new CodeReviewJobRequest
        {
            RepositoryKey = repositoryKey,
            SourceBranch = sourceBranch,
            PrNumber = prNumber,
            RequestedBy = "CodeReviewer",
        }, cancellationToken);
    }
}
