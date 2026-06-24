namespace DevAgent.Hub.Api.Application;

using DevAgent.Audit;
using DevAgent.Contracts.Jobs;

/// <summary>
/// Hub-level application service. It records high-level job intent, audits it,
/// and forwards to the Runner. It does NOT perform allowlist validation itself
/// — that is the Runner's authoritative responsibility — but it is the clean
/// seam between the API layer and the platform workflow.
/// </summary>
public sealed class HubJobApplicationService
{
    private readonly IRunnerClient _runner;
    private readonly IAuditLog _audit;

    public HubJobApplicationService(IRunnerClient runner, IAuditLog audit)
    {
        _runner = runner;
        _audit = audit;
    }

    public async Task<AgentJobResult> StartNuGetUpdateAsync(
        string repositoryKey,
        string packageId,
        string targetVersion,
        string requestedBy,
        CancellationToken cancellationToken = default)
    {
        var request = new NuGetUpdateJobRequest
        {
            RepositoryKey = repositoryKey,
            PackageId = packageId,
            TargetVersion = targetVersion,
            RequestedBy = requestedBy,
        };

        await _audit.WriteAsync(new JobAuditEvent
        {
            JobId = request.JobId,
            Actor = requestedBy,
            Status = nameof(AgentJobStatus.Pending),
            Message = $"Hub accepted NuGet update {packageId}@{targetVersion} for '{repositoryKey}'.",
        }, cancellationToken);

        var result = await _runner.StartNuGetUpdateAsync(request, cancellationToken);

        await _audit.WriteAsync(new JobAuditEvent
        {
            JobId = result.JobId,
            Actor = requestedBy,
            Status = result.Status.ToString(),
            Message = result.Message,
        }, cancellationToken);

        return result;
    }

    public async Task<AgentJobResult> StartDotNetUpgradeAsync(
        string repositoryKey,
        string targetFramework,
        string requestedBy,
        CancellationToken cancellationToken = default)
    {
        var request = new DotNetUpgradeJobRequest
        {
            RepositoryKey = repositoryKey,
            TargetFramework = targetFramework,
            RequestedBy = requestedBy,
        };

        await _audit.WriteAsync(new JobAuditEvent
        {
            JobId = request.JobId,
            Actor = requestedBy,
            Status = nameof(AgentJobStatus.Pending),
            Message = $"Hub accepted .NET upgrade to {targetFramework} for '{repositoryKey}'.",
        }, cancellationToken);

        var result = await _runner.StartDotNetUpgradeAsync(request, cancellationToken);

        await _audit.WriteAsync(new JobAuditEvent
        {
            JobId = result.JobId,
            Actor = requestedBy,
            Status = result.Status.ToString(),
            Message = result.Message,
        }, cancellationToken);

        return result;
    }
}
