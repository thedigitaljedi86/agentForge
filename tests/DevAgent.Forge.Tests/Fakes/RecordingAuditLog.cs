namespace DevAgent.Forge.Tests.Fakes;

using DevAgent.Audit;

/// <summary>Audit log that captures every event for assertions.</summary>
public sealed class RecordingAuditLog : IAuditLog
{
    public List<AuditEvent> Events { get; } = new();

    public Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        Events.Add(auditEvent);
        return Task.CompletedTask;
    }

    public IEnumerable<ToolCallAuditEvent> ToolCalls => Events.OfType<ToolCallAuditEvent>();
    public IEnumerable<DiffAuditEvent> Diffs => Events.OfType<DiffAuditEvent>();
    public IEnumerable<PromptAuditEvent> Prompts => Events.OfType<PromptAuditEvent>();
}
