namespace DevAgent.Hub.Api.Jobs;

using DevAgent.Audit;

/// <summary>
/// Placeholder Hangfire recurring job that will eventually poll for new package
/// versions and enqueue DependencyPilot workflows. For the first milestone it
/// only logs an audit heartbeat so the scheduling plumbing is demonstrable.
///
/// The real detection logic lives in the DependencyPilot agent, not here — the
/// Hub just provides the schedule + trigger.
/// </summary>
public sealed class PackageUpdateCheckJob
{
    private readonly IAuditLog _audit;

    public PackageUpdateCheckJob(IAuditLog audit)
    {
        _audit = audit;
    }

    // Invoked by Hangfire on a recurring schedule.
    public async Task RunAsync()
    {
        await _audit.WriteAsync(new JobAuditEvent
        {
            Actor = nameof(PackageUpdateCheckJob),
            Status = "heartbeat",
            Message = "Placeholder package-update check ran. Real detection arrives with DependencyPilot wiring.",
        });
    }
}
