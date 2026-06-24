namespace DevAgent.Contracts.Jobs;

/// <summary>
/// High-level lifecycle state of a job as tracked by the Hub and Runner.
/// </summary>
public enum AgentJobStatus
{
    Unknown = 0,

    /// <summary>Accepted by the Hub but not yet validated/dispatched.</summary>
    Pending = 1,

    /// <summary>Passed Runner validation; a worker is being prepared.</summary>
    Validated = 2,

    /// <summary>A sandbox worker is currently executing the job.</summary>
    Running = 3,

    /// <summary>Completed successfully (e.g. a pull request was opened).</summary>
    Succeeded = 4,

    /// <summary>Completed but produced no change (e.g. already up to date).</summary>
    NoChange = 5,

    /// <summary>The worker ran but the result was a failure (build/test failed).</summary>
    Failed = 6,

    /// <summary>Rejected before execution (policy/allowlist violation).</summary>
    Rejected = 7,
}
