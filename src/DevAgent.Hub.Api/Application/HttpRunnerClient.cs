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

        using var response = await _http.PostAsJsonAsync("/runner/jobs/nuget-update", body, cancellationToken);

        var result = await response.Content.ReadFromJsonAsync<AgentJobResult>(cancellationToken: cancellationToken);
        return result ?? new AgentJobResult
        {
            JobId = request.JobId,
            Status = AgentJobStatus.Failed,
            Message = $"Runner returned an unreadable response ({(int)response.StatusCode}).",
        };
    }
}
