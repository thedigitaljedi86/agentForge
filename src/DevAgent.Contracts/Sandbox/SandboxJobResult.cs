namespace DevAgent.Contracts.Sandbox;

using DevAgent.Contracts.Jobs;

/// <summary>
/// Result returned by a sandbox worker run to the Runner.
/// </summary>
public sealed record SandboxJobResult
{
    public required string JobId { get; init; }

    public required AgentJobStatus Status { get; init; }

    public string? Message { get; init; }

    /// <summary>Branch the worker created and pushed, if any.</summary>
    public string? BranchName { get; init; }

    /// <summary>Pull request URL produced by the worker, if any.</summary>
    public string? PullRequestUrl { get; init; }

    /// <summary>Whether restore/build/test all succeeded.</summary>
    public bool BuildSucceeded { get; init; }

    public bool TestsSucceeded { get; init; }

    public DateTimeOffset CompletedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
