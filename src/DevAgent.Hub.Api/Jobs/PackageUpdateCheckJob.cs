namespace DevAgent.Hub.Api.Jobs;

using Agents.DependencyPilot;
using DevAgent.Audit;

/// <summary>
/// Hangfire recurring job: ask DependencyPilot to check its watched packages
/// for new versions and start an update workflow for every affected
/// repository. Every started workflow still passes the Runner's allowlist
/// gate — the schedule grants no extra authority.
/// </summary>
public sealed class PackageUpdateCheckJob
{
    private readonly DependencyPilotService _dependencyPilot;
    private readonly IAuditLog _audit;

    public PackageUpdateCheckJob(DependencyPilotService dependencyPilot, IAuditLog audit)
    {
        _dependencyPilot = dependencyPilot;
        _audit = audit;
    }

    // Invoked by Hangfire on a recurring schedule.
    public async Task RunAsync()
    {
        var candidates = await _dependencyPilot.CheckForPackageUpdatesAsync();

        await _audit.WriteAsync(new JobAuditEvent
        {
            Actor = nameof(PackageUpdateCheckJob),
            Status = "scan",
            Message = $"Package update check found {candidates.Count} candidate(s).",
        });

        foreach (var candidate in candidates)
        {
            await _dependencyPilot.StartDependencyUpdateWorkflowAsync(candidate);
        }
    }
}
