namespace DevAgent.Contracts.Jobs;

/// <summary>
/// High-level result returned to the Hub for a dispatched job.
/// </summary>
public sealed record AgentJobResult
{
    public required string JobId { get; init; }

    public required AgentJobStatus Status { get; init; }

    /// <summary>Human-readable summary of what happened.</summary>
    public string? Message { get; init; }

    /// <summary>URL of the pull request produced, if any. Never an auto-merge link.</summary>
    public string? PullRequestUrl { get; init; }

    /// <summary>When the result was produced (UTC).</summary>
    public DateTimeOffset CompletedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public static AgentJobResult Rejected(string jobId, string reason) => new()
    {
        JobId = jobId,
        Status = AgentJobStatus.Rejected,
        Message = reason,
    };
}
