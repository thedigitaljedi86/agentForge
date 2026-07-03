namespace DevAgent.Audit;

using System.Collections.Concurrent;

/// <summary>
/// Keeps the most recent audit events in memory so UIs can show live evidence.
/// This is a WINDOW for operators, not the durable audit trail — pair it with
/// a persistent sink via <see cref="CompositeAuditLog"/>.
/// </summary>
public sealed class InMemoryRingAuditLog : IAuditLog
{
    private readonly ConcurrentQueue<AuditEvent> _events = new();
    private readonly int _capacity;

    public InMemoryRingAuditLog(int capacity = 500)
    {
        _capacity = capacity;
    }

    public Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        _events.Enqueue(auditEvent);
        while (_events.Count > _capacity && _events.TryDequeue(out _))
        {
        }

        return Task.CompletedTask;
    }

    /// <summary>Most recent events, newest first.</summary>
    public IReadOnlyList<AuditEvent> Snapshot(int take = 200) =>
        _events.Reverse().Take(take).ToList();
}

/// <summary>Fans every event out to multiple sinks (e.g. console + ring buffer).</summary>
public sealed class CompositeAuditLog : IAuditLog
{
    private readonly IReadOnlyList<IAuditLog> _sinks;

    public CompositeAuditLog(params IAuditLog[] sinks)
    {
        _sinks = sinks;
    }

    public async Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        foreach (var sink in _sinks)
        {
            await sink.WriteAsync(auditEvent, cancellationToken);
        }
    }
}
