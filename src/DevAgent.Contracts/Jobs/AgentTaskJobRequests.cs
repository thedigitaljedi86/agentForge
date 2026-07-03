namespace DevAgent.Contracts.Jobs;

/// <summary>
/// Request to diagnose and fix a failing CI pipeline for an allowlisted
/// repository. As everywhere: repository by KEY; the branch name is validated
/// by the Runner; the failure log is context for the repair agent, never
/// instructions with authority (the agent stays fully caged regardless).
/// </summary>
public sealed record PipelineFixJobRequest : AgentJobRequest
{
    public override AgentJobType JobType => AgentJobType.PipelineFix;

    public required string RepositoryKey { get; init; }

    /// <summary>The branch whose pipeline failed (validated ref name).</summary>
    public required string Branch { get; init; }

    /// <summary>CI failure log tail (truncated) used as repair context.</summary>
    public string FailureContext { get; init; } = string.Empty;
}

/// <summary>Request to create/refresh documentation for a repository.</summary>
public sealed record DocUpdateJobRequest : AgentJobRequest
{
    public override AgentJobType JobType => AgentJobType.DocUpdate;

    public required string RepositoryKey { get; init; }
}

/// <summary>
/// Request to review a pull request's changes. The review agent is READ-ONLY
/// by construction — its output is a review comment, never a code change.
/// </summary>
public sealed record CodeReviewJobRequest : AgentJobRequest
{
    public override AgentJobType JobType => AgentJobType.CodeReview;

    public required string RepositoryKey { get; init; }

    /// <summary>The PR's source branch (validated ref name).</summary>
    public required string SourceBranch { get; init; }

    /// <summary>Provider PR number, when known (used for the comment).</summary>
    public int? PrNumber { get; init; }
}
