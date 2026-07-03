namespace DevAgent.Hub.Api.Jobs;

using Agents.CodeReviewer;
using Agents.DocScribe;
using Agents.PipelineDoctor;
using DevAgent.Audit;
using DevAgent.Contracts.Jobs;
using DevAgent.Hub.Api.Application;

/// <summary>
/// Hub implementations of the new agents' trigger seams. Each forwards the
/// proposal to the Runner (the authoritative allowlist gate), records the job
/// on the dashboard, and turns transport failures into Failed results so
/// schedules and webhooks degrade gracefully.
/// </summary>
public sealed class HubPipelineFixTrigger : IPipelineFixTrigger
{
    private readonly IRunnerClient _runner;
    private readonly IJobTracker _tracker;
    private readonly IAuditLog _audit;

    public HubPipelineFixTrigger(IRunnerClient runner, IJobTracker tracker, IAuditLog audit)
    {
        _runner = runner;
        _tracker = tracker;
        _audit = audit;
    }

    public async Task<AgentJobResult> StartPipelineFixAsync(
        PipelineFixJobRequest request, CancellationToken cancellationToken = default)
    {
        var target = $"{request.RepositoryKey} @ {request.Branch}";
        _tracker.Upsert(request.JobId, "PipelineDoctor", "PipelineFix", target, AgentJobStatus.Pending, "Failure detected; repair proposed.");
        await _audit.WriteAsync(new JobAuditEvent
        {
            JobId = request.JobId,
            Actor = "PipelineDoctor",
            Status = nameof(AgentJobStatus.Pending),
            Message = $"Pipeline repair proposed for '{request.RepositoryKey}' branch '{request.Branch}'.",
        }, cancellationToken);

        var result = await _runner.StartPipelineFixAsync(request, cancellationToken);

        _tracker.Upsert(result.JobId, "PipelineDoctor", "PipelineFix", target, result.Status, result.Message);
        await _audit.WriteAsync(new JobAuditEvent
        {
            JobId = result.JobId,
            Actor = "PipelineDoctor",
            Status = result.Status.ToString(),
            Message = result.Message,
        }, cancellationToken);

        return result;
    }
}

/// <summary>Hub implementation of DocScribe's trigger seam.</summary>
public sealed class HubDocUpdateTrigger : IDocUpdateTrigger
{
    private readonly IRunnerClient _runner;
    private readonly IJobTracker _tracker;
    private readonly IAuditLog _audit;

    public HubDocUpdateTrigger(IRunnerClient runner, IJobTracker tracker, IAuditLog audit)
    {
        _runner = runner;
        _tracker = tracker;
        _audit = audit;
    }

    public async Task<AgentJobResult> StartDocUpdateAsync(
        DocUpdateJobRequest request, CancellationToken cancellationToken = default)
    {
        _tracker.Upsert(request.JobId, "DocScribe", "DocUpdate", request.RepositoryKey, AgentJobStatus.Pending, "Documentation refresh proposed.");
        await _audit.WriteAsync(new JobAuditEvent
        {
            JobId = request.JobId,
            Actor = "DocScribe",
            Status = nameof(AgentJobStatus.Pending),
            Message = $"Documentation refresh proposed for '{request.RepositoryKey}'.",
        }, cancellationToken);

        var result = await _runner.StartDocUpdateAsync(request, cancellationToken);

        _tracker.Upsert(result.JobId, "DocScribe", "DocUpdate", request.RepositoryKey, result.Status, result.Message);
        await _audit.WriteAsync(new JobAuditEvent
        {
            JobId = result.JobId,
            Actor = "DocScribe",
            Status = result.Status.ToString(),
            Message = result.Message,
        }, cancellationToken);

        return result;
    }
}

/// <summary>Hub implementation of CodeReviewer's trigger seam.</summary>
public sealed class HubCodeReviewTrigger : ICodeReviewTrigger
{
    private readonly IRunnerClient _runner;
    private readonly IJobTracker _tracker;
    private readonly IAuditLog _audit;

    public HubCodeReviewTrigger(IRunnerClient runner, IJobTracker tracker, IAuditLog audit)
    {
        _runner = runner;
        _tracker = tracker;
        _audit = audit;
    }

    public async Task<AgentJobResult> StartCodeReviewAsync(
        CodeReviewJobRequest request, CancellationToken cancellationToken = default)
    {
        var target = request.PrNumber is int pr
            ? $"{request.RepositoryKey} PR #{pr} ({request.SourceBranch})"
            : $"{request.RepositoryKey} ({request.SourceBranch})";
        _tracker.Upsert(request.JobId, "CodeReviewer", "CodeReview", target, AgentJobStatus.Pending, "Review proposed.");
        await _audit.WriteAsync(new JobAuditEvent
        {
            JobId = request.JobId,
            Actor = "CodeReviewer",
            Status = nameof(AgentJobStatus.Pending),
            Message = $"Code review proposed for {target}.",
        }, cancellationToken);

        var result = await _runner.StartCodeReviewAsync(request, cancellationToken);

        _tracker.Upsert(result.JobId, "CodeReviewer", "CodeReview", target, result.Status, result.Message);
        await _audit.WriteAsync(new JobAuditEvent
        {
            JobId = result.JobId,
            Actor = "CodeReviewer",
            Status = result.Status.ToString(),
            Message = result.Message,
        }, cancellationToken);

        return result;
    }
}
