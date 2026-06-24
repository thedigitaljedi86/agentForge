namespace DevAgent.Bridge.Git;

/// <summary>A repository as understood by a Git hosting provider.</summary>
public sealed record GitRepository
{
    public required string CloneUrl { get; init; }
    public required string DefaultBranch { get; init; }

    /// <summary>Provider-neutral identifier (e.g. "org/repo"). Optional.</summary>
    public string? FullName { get; init; }
}

/// <summary>Request to open a pull request. Never an auto-merge request.</summary>
public sealed record PullRequestRequest
{
    public required string SourceBranch { get; init; }
    public required string TargetBranch { get; init; }
    public required string Title { get; init; }
    public string Body { get; init; } = string.Empty;

    /// <summary>
    /// Always false in this platform. Present so the type is explicit about it;
    /// implementations must reject any attempt to set this true.
    /// SECURITY: auto-merge is not supported — human review is mandatory.
    /// </summary>
    public bool AutoMerge { get; init; } = false;
}

/// <summary>Result of opening a pull request.</summary>
public sealed record PullRequestResult
{
    public required bool Created { get; init; }
    public string? Url { get; init; }
    public string? Message { get; init; }
}

/// <summary>
/// Provider-neutral Git operations. Concrete implementations (GitHub, GitLab,
/// Azure DevOps, Bitbucket) come later. The worker depends only on this
/// abstraction so swapping providers needs no worker changes.
/// </summary>
public interface IGitProvider
{
    /// <summary>Resolves a trusted clone URL to provider metadata.</summary>
    Task<GitRepository> GetRepositoryAsync(string cloneUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a pull request. Implementations MUST refuse to auto-merge and
    /// MUST rely on the provider's branch protection + human review.
    /// </summary>
    Task<PullRequestResult> CreatePullRequestAsync(
        GitRepository repository,
        PullRequestRequest request,
        CancellationToken cancellationToken = default);
}
