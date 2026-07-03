namespace DevAgent.Hub.Api.Jobs;

using Agents.DotNetUpgrader;
using DevAgent.Audit;
using DevAgent.Contracts.Jobs;
using DevAgent.Hub.Api.Application;

/// <summary>
/// The Hub's implementation of the DotNetUpgrader agent's trigger seam. It
/// forwards a proposed upgrade to the Runner (which performs the authoritative
/// allowlist validation) and records the job on the status dashboard. Runner
/// transport failures are turned into a Failed result rather than crashing the
/// scheduled job, so the dashboard always reflects reality.
/// </summary>
public sealed class HubDotNetUpgradeTrigger : IDotNetUpgradeTrigger
{
    private const string AgentName = "DotNetUpgrader (scheduled)";

    private readonly IRunnerClient _runner;
    private readonly IJobTracker _tracker;
    private readonly IAuditLog _audit;

    public HubDotNetUpgradeTrigger(IRunnerClient runner, IJobTracker tracker, IAuditLog audit)
    {
        _runner = runner;
        _tracker = tracker;
        _audit = audit;
    }

    public async Task<AgentJobResult> StartDotNetUpgradeAsync(
        DotNetUpgradeJobRequest request,
        CancellationToken cancellationToken = default)
    {
        var target = $"{request.RepositoryKey} → {request.TargetFramework}";
        _tracker.Upsert(request.JobId, AgentName, "DotNetUpgrade", target, AgentJobStatus.Pending, "Proposed by scheduled agent.");
        await _audit.WriteAsync(new JobAuditEvent
        {
            JobId = request.JobId,
            Actor = AgentName,
            Status = nameof(AgentJobStatus.Pending),
            Message = $"Scheduled .NET upgrade to {request.TargetFramework} for '{request.RepositoryKey}'.",
        }, cancellationToken);

        AgentJobResult result;
        try
        {
            result = await _runner.StartDotNetUpgradeAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            result = new AgentJobResult
            {
                JobId = request.JobId,
                Status = AgentJobStatus.Failed,
                Message = $"Runner unreachable: {ex.Message}",
            };
        }

        _tracker.Upsert(result.JobId, AgentName, "DotNetUpgrade", target, result.Status, result.Message);
        await _audit.WriteAsync(new JobAuditEvent
        {
            JobId = result.JobId,
            Actor = AgentName,
            Status = result.Status.ToString(),
            Message = result.Message,
        }, cancellationToken);

        return result;
    }
}
