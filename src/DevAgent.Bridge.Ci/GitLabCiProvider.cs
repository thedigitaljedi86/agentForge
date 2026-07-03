namespace DevAgent.Bridge.Ci;

using System.Text.Json.Nodes;

/// <summary>
/// GitLab CI via the REST API:
///   runs: GET {base}/api/v4/projects/{url-encoded path}/pipelines?status=failed
///   logs: GET .../pipelines/{id}/jobs → failed jobs → GET .../jobs/{jobId}/trace
/// Auth: "PRIVATE-TOKEN: {token}".
/// </summary>
public sealed class GitLabCiProvider : ICiProvider
{
    private readonly HttpClient _http;
    private readonly Func<string, string?> _readEnv;

    public GitLabCiProvider(HttpClient http, Func<string, string?>? readEnv = null)
    {
        _http = http;
        _readEnv = readEnv ?? Environment.GetEnvironmentVariable;
    }

    public async Task<IReadOnlyList<CiPipelineRun>> ListFailedRunsAsync(
        CiConnection connection, int top = 10, CancellationToken ct = default)
    {
        var url = $"{Project(connection)}/pipelines?status=failed&per_page={top}&order_by=updated_at";
        var json = JsonNode.Parse(await GetTextAsync(connection, url, ct));

        var runs = new List<CiPipelineRun>();
        foreach (var run in json as JsonArray ?? new JsonArray())
        {
            runs.Add(new CiPipelineRun
            {
                RunId = run?["id"]?.ToString() ?? string.Empty,
                Branch = (string?)run?["ref"] ?? string.Empty,
                Title = (string?)run?["name"] ?? $"pipeline {run?["id"]}",
                WebUrl = (string?)run?["web_url"] ?? string.Empty,
                FinishedUtc = DateTimeOffset.TryParse((string?)run?["updated_at"], out var t) ? t : null,
            });
        }

        return runs;
    }

    public async Task<string> GetFailureLogAsync(CiConnection connection, string runId, CancellationToken ct = default)
    {
        var jobsUrl = $"{Project(connection)}/pipelines/{Uri.EscapeDataString(runId)}/jobs";
        var jobs = JsonNode.Parse(await GetTextAsync(connection, jobsUrl, ct));

        var sb = new System.Text.StringBuilder();
        foreach (var job in jobs as JsonArray ?? new JsonArray())
        {
            if ((string?)job?["status"] != "failed")
            {
                continue;
            }

            sb.AppendLine($"== job: {(string?)job?["name"]} ==");
            var jobId = job?["id"]?.ToString();
            if (jobId is not null)
            {
                var traceUrl = $"{Base(connection)}/api/v4/projects/{EncodedPath(connection)}/jobs/{jobId}/trace";
                sb.AppendLine(await GetTextAsync(connection, traceUrl, ct));
            }
        }

        return CiLog.TruncateTail(sb.ToString());
    }

    private static string Base(CiConnection c) => c.BaseUrl.TrimEnd('/');

    private static string EncodedPath(CiConnection c) => Uri.EscapeDataString(c.ProjectPath);

    private static string Project(CiConnection c) => $"{Base(c)}/api/v4/projects/{EncodedPath(c)}";

    private async Task<string> GetTextAsync(CiConnection c, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        var token = c.TokenEnvVar is null ? null : _readEnv(c.TokenEnvVar);
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.TryAddWithoutValidation("PRIVATE-TOKEN", token);
        }

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }
}
