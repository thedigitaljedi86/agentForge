namespace DevAgent.Audit;

/// <summary>
/// Append-only audit sink. Implementations must never mutate or delete events.
/// First milestone ships a console implementation; production would write to a
/// durable, tamper-evident store (e.g. append-only DB table or log pipeline).
/// </summary>
public interface IAuditLog
{
    Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);
}

/// <summary>
/// Simple console audit sink for the first milestone. Useful for local runs
/// and tests; intentionally trivial so it has no external dependencies.
/// </summary>
public sealed class ConsoleAuditLog : IAuditLog
{
    public Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        Console.WriteLine(
            $"[AUDIT {auditEvent.TimestampUtc:O}] {auditEvent.Kind} job={auditEvent.JobId} actor={auditEvent.Actor} :: {auditEvent}");
        return Task.CompletedTask;
    }
}
