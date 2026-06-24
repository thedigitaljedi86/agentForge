namespace DevAgent.Audit;

/// <summary>Category of an audit event, used for filtering and retention.</summary>
public enum AuditEventKind
{
    Decision = 0,
    Job = 1,
    ToolCall = 2,
    Diff = 3,
    Prompt = 4,
}

/// <summary>
/// Base audit event. Audit events are append-only and should be treated as
/// immutable evidence of what the platform decided and did.
/// </summary>
public abstract record AuditEvent
{
    public string EventId { get; init; } = Guid.NewGuid().ToString("N");

    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Job this event belongs to, for correlation.</summary>
    public string? JobId { get; init; }

    /// <summary>Actor responsible (service name, user, schedule).</summary>
    public string Actor { get; init; } = "system";

    public abstract AuditEventKind Kind { get; }
}

/// <summary>Records a job lifecycle transition (created, validated, completed).</summary>
public sealed record JobAuditEvent : AuditEvent
{
    public override AuditEventKind Kind => AuditEventKind.Job;
    public required string Status { get; init; }
    public string? Message { get; init; }
}

/// <summary>
/// Records a single tool call made by a (future) coding agent. Captured so we
/// can prove the agent only used policy-approved, structured tools.
/// </summary>
public sealed record ToolCallAuditEvent : AuditEvent
{
    public override AuditEventKind Kind => AuditEventKind.ToolCall;
    public required string ToolName { get; init; }
    public string? Arguments { get; init; }
    public bool Allowed { get; init; }
    public string? DenyReason { get; init; }
}

/// <summary>Records a generated diff so all code changes are reviewable.</summary>
public sealed record DiffAuditEvent : AuditEvent
{
    public override AuditEventKind Kind => AuditEventKind.Diff;
    public required string FilePath { get; init; }
    public required string UnifiedDiff { get; init; }
}

/// <summary>Records a prompt sent to an LLM (future Forge use).</summary>
public sealed record PromptAuditEvent : AuditEvent
{
    public override AuditEventKind Kind => AuditEventKind.Prompt;
    public required string Prompt { get; init; }
    public string? Model { get; init; }
}

/// <summary>Records a security/policy decision (allow or deny).</summary>
public sealed record DecisionAuditEvent : AuditEvent
{
    public override AuditEventKind Kind => AuditEventKind.Decision;
    public required string Decision { get; init; }
    public required bool Allowed { get; init; }
    public string? Reason { get; init; }
}
