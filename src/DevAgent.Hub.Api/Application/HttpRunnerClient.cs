namespace DevAgent.Hub.Api.Application;

using System.Net.Http.Json;
using DevAgent.Contracts.Jobs;

/// <summary>
/// HTTP implementation of <see cref="IRunnerClient"/>. It forwards only the
/// safe, typed fields (keys + version) to the Runner. The Runner performs the
/// authoritative allowlist validation — the Hub does not get to bypass it.
/// </summary>
public sealed class HttpRunnerClient : IRunnerClient
{
    private readonly HttpClient _http;

    public HttpRunnerClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<AgentJobResult> StartNuGetUpdateAsync(
        NuGetUpdateJobRequest request,
        CancellationToken cancellationToken = default)
    {
        var body = new
        {
            jobId = request.JobId,
            repositoryKey = request.RepositoryKey,
            packageId = request.PackageId,
            targetVersion = request.TargetVersion,
            onlyUpgrade = request.OnlyUpgrade,
            requestedBy = request.RequestedBy,
        };

        // An unreachable Runner is a FAILED job, not an unhandled exception —
        // webhooks and schedules must degrade gracefully and leave a record.
        try
        {
            using var response = await _http.PostAsJsonAsync("/runner/jobs/nuget-update", body, cancellationToken);

            var result = await response.Content.ReadFromJsonAsync<AgentJobResult>(cancellationToken: cancellationToken);
            return result ?? new AgentJobResult
            {
                JobId = request.JobId,
                Status = AgentJobStatus.Failed,
                Message = $"Runner returned an unreadable response ({(int)response.StatusCode}).",
            };
        }
        catch (HttpRequestException ex)
        {
            return new AgentJobResult
            {
                JobId = request.JobId,
                Status = AgentJobStatus.Failed,
                Message = $"Runner unreachable: {ex.Message}",
            };
        }
    }

    public async Task<AgentJobResult> StartDotNetUpgradeAsync(
        DotNetUpgradeJobRequest request,
        CancellationToken cancellationToken = default)
    {
        var body = new
        {
            jobId = request.JobId,
            repositoryKey = request.RepositoryKey,
            targetFramework = request.TargetFramework,
            onlyUpgrade = request.OnlyUpgrade,
            requestedBy = request.RequestedBy,
        };

        // An unreachable Runner is a FAILED job, not an unhandled exception —
        // webhooks and schedules must degrade gracefully and leave a record.
        try
        {
            using var response = await _http.PostAsJsonAsync("/runner/jobs/dotnet-upgrade", body, cancellationToken);

            var result = await response.Content.ReadFromJsonAsync<AgentJobResult>(cancellationToken: cancellationToken);
            return result ?? new AgentJobResult
            {
                JobId = request.JobId,
                Status = AgentJobStatus.Failed,
                Message = $"Runner returned an unreadable response ({(int)response.StatusCode}).",
            };
        }
        catch (HttpRequestException ex)
        {
            return new AgentJobResult
            {
                JobId = request.JobId,
                Status = AgentJobStatus.Failed,
                Message = $"Runner unreachable: {ex.Message}",
            };
        }
    }
}
