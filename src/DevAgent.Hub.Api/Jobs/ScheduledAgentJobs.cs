namespace DevAgent.Hub.Api.Jobs;

using Agents.ConfluenceGuide;
using Agents.DocScribe;
using Agents.PipelineDoctor;
using Agents.SplunkSentinel;
using DevAgent.Audit;

/// <summary>
/// PipelineDoctor's schedule: every sweep checks each watched repository's CI
/// for NEW failed runs (dedupe via the processed-run store) and proposes a
/// repair per failure. All proposals go through the Runner's allowlist gate.
/// </summary>
public sealed class PipelineDoctorSweepJob
{
    private readonly PipelineDoctorService _service;
    private readonly IAuditLog _audit;

    public PipelineDoctorSweepJob(PipelineDoctorService service, IAuditLog audit)
    {
        _service = service;
        _audit = audit;
    }

    public async Task RunAsync()
    {
        var findings = await _service.SweepAsync();

        await _audit.WriteAsync(new JobAuditEvent
        {
            Actor = nameof(PipelineDoctorSweepJob),
            Status = "scan",
            Message = $"CI sweep complete: {findings.Count} new failure(s) handled.",
        });
    }
}

/// <summary>
/// DocScribe's schedule: the weekly documentation-maintenance sweep. Each
/// watched repository gets a DocUpdate job; a PR appears only when the docs
/// actually changed, so a quiet week costs nothing.
/// </summary>
public sealed class ScheduledDocUpdateJob
{
    private readonly DocScribeService _service;
    private readonly IAuditLog _audit;

    public ScheduledDocUpdateJob(DocScribeService service, IAuditLog audit)
    {
        _service = service;
        _audit = audit;
    }

    public async Task RunAsync()
    {
        var results = await _service.SweepAsync();

        await _audit.WriteAsync(new JobAuditEvent
        {
            Actor = nameof(ScheduledDocUpdateJob),
            Status = "scan",
            Message = $"Documentation sweep complete: {results.Count} repositories processed.",
        });
    }
}

/// <summary>SplunkSentinel's schedule: run the configured searches and audit the findings.</summary>
public sealed class SplunkSentinelSweepJob
{
    private readonly SplunkSentinelService _service;

    public SplunkSentinelSweepJob(SplunkSentinelService service) => _service = service;

    public Task RunAsync() => _service.SweepAsync();
}

/// <summary>ConfluenceGuide's schedule: recompute and audit the docs→page sync plan (read-only).</summary>
public sealed class ConfluenceSyncPlanJob
{
    private readonly ConfluenceGuideService _service;

    public ConfluenceSyncPlanJob(ConfluenceGuideService service) => _service = service;

    public Task RunAsync() => _service.PlanSyncAsync();
}
