namespace DevAgent.Bridge.Ci;

using System.Text;
using System.Text.Json.Nodes;

/// <summary>
/// Azure DevOps Pipelines via the REST API:
///   runs: GET {base}/{org}/{project}/_apis/build/builds?resultFilter=failed&$top=N&api-version=7.1
///   logs: GET .../builds/{id}/logs?api-version=7.1 → last log ids
///         → GET .../builds/{id}/logs/{logId}?api-version=7.1 (text)
/// Auth: "Authorization: Basic base64(:PAT)".
/// </summary>
public sealed class AzureDevOpsCiProvider : ICiProvider
{
    private readonly HttpClient _http;
    private readonly Func<string, string?> _readEnv;

    public AzureDevOpsCiProvider(HttpClient http, Func<string, string?>? readEnv = null)
    {
        _http = http;
        _readEnv = readEnv ?? Environment.GetEnvironmentVariable;
    }

    public async Task<IReadOnlyList<CiPipelineRun>> ListFailedRunsAsync(
        CiConnection connection, int top = 10, CancellationToken ct = default)
    {
        var url = $"{Project(connection)}/_apis/build/builds?resultFilter=failed&$top={top}&api-version=7.1";
        var json = JsonNode.Parse(await GetTextAsync(connection, url, ct));

        var runs = new List<CiPipelineRun>();
        foreach (var build in json?["value"] as JsonArray ?? new JsonArray())
        {
            var branch = (string?)build?["sourceBranch"] ?? string.Empty;
            runs.Add(new CiPipelineRun
            {
                RunId = build?["id"]?.ToString() ?? string.Empty,
                // Azure reports refs/heads/main — normalise to the branch name.
                Branch = branch.StartsWith("refs/heads/", StringComparison.Ordinal) ? branch[11..] : branch,
                Title = (string?)build?["definition"]?["name"] ?? (string?)build?["buildNumber"] ?? string.Empty,
                WebUrl = (string?)build?["_links"]?["web"]?["href"] ?? string.Empty,
                FinishedUtc = DateTimeOffset.TryParse((string?)build?["finishTime"], out var t) ? t : null,
            });
        }

        return runs;
    }

    public async Task<string> GetFailureLogAsync(CiConnection connection, string runId, CancellationToken ct = default)
    {
        var listUrl = $"{Project(connection)}/_apis/build/builds/{Uri.EscapeDataString(runId)}/logs?api-version=7.1";
        var logs = JsonNode.Parse(await GetTextAsync(connection, listUrl, ct));

        // Azure doesn't flag which log failed; take the last few (the failure
        // is at the end of the timeline) and let the tail-truncation focus it.
        var ids = (logs?["value"] as JsonArray ?? new JsonArray())
            .Select(l => l?["id"]?.ToString())
            .Where(id => id is not null)
            .TakeLast(3)
            .ToList();

        var sb = new StringBuilder();
        foreach (var id in ids)
        {
            var logUrl = $"{Project(connection)}/_apis/build/builds/{Uri.EscapeDataString(runId)}/logs/{id}?api-version=7.1";
            sb.AppendLine(await GetTextAsync(connection, logUrl, ct));
        }

        return CiLog.TruncateTail(sb.ToString());
    }

    private static string Project(CiConnection c) => $"{c.BaseUrl.TrimEnd('/')}/{c.ProjectPath}";

    private async Task<string> GetTextAsync(CiConnection c, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        var token = c.TokenEnvVar is null ? null : _readEnv(c.TokenEnvVar);
        if (!string.IsNullOrEmpty(token))
        {
            var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{token}"));
            request.Headers.TryAddWithoutValidation("Authorization", $"Basic {basic}");
        }

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>Resolves the right provider implementation for a connection. Virtual for test fakes.</summary>
public class CiProviderFactory
{
    private readonly HttpClient _http;
    private readonly Func<string, string?> _readEnv;

    public CiProviderFactory(HttpClient http, Func<string, string?>? readEnv = null)
    {
        _http = http;
        _readEnv = readEnv ?? Environment.GetEnvironmentVariable;
    }

    public virtual ICiProvider Create(CiProviderKind kind) => kind switch
    {
        CiProviderKind.GitHubActions => new GitHubActionsCiProvider(_http, _readEnv),
        CiProviderKind.GitLabCi => new GitLabCiProvider(_http, _readEnv),
        CiProviderKind.AzureDevOpsPipelines => new AzureDevOpsCiProvider(_http, _readEnv),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported CI provider."),
    };
}
