namespace DevAgent.Bridge.Git;

using DevAgent.Contracts.Validation;

/// <summary>
/// Placeholder provider for the first milestone. It performs no network calls;
/// it returns a deterministic "would create PR" result so the end-to-end flow
/// can be exercised without a real provider configured.
///
/// SECURITY: Even the placeholder refuses auto-merge — this keeps the
/// no-auto-merge invariant true regardless of which provider is wired in.
/// </summary>
public sealed class PlaceholderGitProvider : IGitProvider
{
    public Task<GitRepository> GetRepositoryAsync(string cloneUrl, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new GitRepository
        {
            CloneUrl = cloneUrl,
            DefaultBranch = "main",
        });
    }

    public Task<PullRequestResult> CreatePullRequestAsync(
        GitRepository repository,
        PullRequestRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.AutoMerge)
        {
            // Hard invariant: this platform never auto-merges.
            throw new PolicyViolationException("Auto-merge is not permitted. A pull request must be reviewed by a human.");
        }

        return Task.FromResult(new PullRequestResult
        {
            Created = true,
            Url = $"(placeholder) PR for {request.SourceBranch} -> {request.TargetBranch}",
            Message = "Placeholder provider did not contact a real Git host.",
        });
    }
}
