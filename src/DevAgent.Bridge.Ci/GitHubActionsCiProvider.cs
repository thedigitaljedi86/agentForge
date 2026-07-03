namespace DevAgent.Bridge.Ci;

using System.Text.Json.Nodes;

/// <summary>
/// GitHub Actions via the REST API:
///   runs: GET {base}/repos/{owner}/{repo}/actions/runs?status=failure
///   logs: GET {base}/repos/{owner}/{repo}/actions/runs/{id}/jobs → failed jobs
///         → GET {base}/repos/{owner}/{repo}/actions/jobs/{jobId}/logs (text)
/// Auth: "Authorization: Bearer {token}".
/// </summary>
public sealed class GitHubActionsCiProvider : ICiProvider
{
    private readonly HttpClient _http;
    private readonly Func<string, string?> _readEnv;

    public GitHubActionsCiProvider(HttpClient http, Func<string, string?>? readEnv = null)
    {
        _http = http;
        _readEnv = readEnv ?? Environment.GetEnvironmentVariable;
    }

    public async Task<IReadOnlyList<CiPipelineRun>> ListFailedRunsAsync(
        CiConnection connection, int top = 10, CancellationToken ct = default)
    {
        var url = $"{Base(connection)}/repos/{connection.ProjectPath}/actions/runs?status=failure&per_page={top}";
        var json = await GetJsonAsync(connection, url, ct);

        var runs = new List<CiPipelineRun>();
        foreach (var run in json?["workflow_runs"] as JsonArray ?? new JsonArray())
        {
            runs.Add(new CiPipelineRun
            {
                RunId = run?["id"]?.ToString() ?? string.Empty,
                Branch = (string?)run?["head_branch"] ?? string.Empty,
                Title = (string?)run?["display_title"] ?? (string?)run?["name"] ?? string.Empty,
                WebUrl = (string?)run?["html_url"] ?? string.Empty,
                FinishedUtc = ParseTime((string?)run?["updated_at"]),
            });
        }

        return runs;
    }

    public async Task<string> GetFailureLogAsync(CiConnection connection, string runId, CancellationToken ct = default)
    {
        var jobsUrl = $"{Base(connection)}/repos/{connection.ProjectPath}/actions/runs/{Uri.EscapeDataString(runId)}/jobs";
        var jobs = await GetJsonAsync(connection, jobsUrl, ct);

        var sb = new System.Text.StringBuilder();
        foreach (var job in jobs?["jobs"] as JsonArray ?? new JsonArray())
        {
            if ((string?)job?["conclusion"] != "failure")
            {
                continue;
            }

            var jobId = job?["id"]?.ToString();
            sb.AppendLine($"== job: {(string?)job?["name"]} ==");
            if (jobId is not null)
            {
                var logUrl = $"{Base(connection)}/repos/{connection.ProjectPath}/actions/jobs/{jobId}/logs";
                sb.AppendLine(await GetTextAsync(connection, logUrl, ct));
            }
        }

        return CiLog.TruncateTail(sb.ToString());
    }

    private static string Base(CiConnection c) => c.BaseUrl.TrimEnd('/');

    private static DateTimeOffset? ParseTime(string? value) =>
        DateTimeOffset.TryParse(value, out var t) ? t : null;

    private async Task<JsonNode?> GetJsonAsync(CiConnection c, string url, CancellationToken ct)
        => JsonNode.Parse(await GetTextAsync(c, url, ct) is { Length: > 0 } s ? s : "{}");

    private async Task<string> GetTextAsync(CiConnection c, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("User-Agent", "DevAgent");

        var token = c.TokenEnvVar is null ? null : _readEnv(c.TokenEnvVar);
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        }

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }
}
