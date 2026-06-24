namespace DevAgent.Contracts.Jobs;

/// <summary>
/// Base shape for any job request entering the platform.
///
/// SECURITY: Note what is deliberately ABSENT here — there is no raw
/// repository URL, no container image, no command line and no Docker
/// arguments. Callers reference repositories and packages by allowlist
/// <c>key</c> only; the Runner resolves those keys to concrete, trusted
/// values. This prevents a caller from injecting arbitrary targets.
/// </summary>
public abstract record AgentJobRequest
{
    /// <summary>Stable identifier for correlating logs and audit events.</summary>
    public string JobId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>The type of job. Validated against the job-type allowlist.</summary>
    public abstract AgentJobType JobType { get; }

    /// <summary>Who/what requested the job (user, schedule, webhook source).</summary>
    public string RequestedBy { get; init; } = "system";

    /// <summary>Free-form correlation id from the triggering source (optional).</summary>
    public string? CorrelationId { get; init; }
}
