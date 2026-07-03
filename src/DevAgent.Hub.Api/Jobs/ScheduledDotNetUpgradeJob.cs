namespace DevAgent.Hub.Api.Jobs;

using Agents.DotNetUpgrader;
using DevAgent.Audit;

/// <summary>
/// EXAMPLE of a scheduled agent. Hangfire invokes this on a recurring schedule;
/// it asks the DotNetUpgrader agent to plan an upgrade for every watched
/// repository and starts each one through the platform. This shows what
/// scheduled agents are for: unattended, time-based fan-out of agent work
/// (nightly framework sweeps, hourly dependency checks, weekly audits) — all
/// still funnelled through the same allowlist-validated, PR-only pipeline.
/// </summary>
public sealed class ScheduledDotNetUpgradeJob
{
    private readonly DotNetUpgradeService _service;
    private readonly IAuditLog _audit;

    public ScheduledDotNetUpgradeJob(DotNetUpgradeService service, IAuditLog audit)
    {
        _service = service;
        _audit = audit;
    }

    public async Task RunAsync()
    {
        var candidates = _service.PlanUpgrades();

        await _audit.WriteAsync(new JobAuditEvent
        {
            Actor = nameof(ScheduledDotNetUpgradeJob),
            Status = "scan",
            Message = $"Scheduled sweep: planning .NET upgrades for {candidates.Count} watched repo(s).",
        });

        foreach (var candidate in candidates)
        {
            await _service.StartUpgradeWorkflowAsync(candidate);
        }
    }
}
