namespace DevAgent.Bridge.Splunk;

/// <summary>
/// An administrator-configured Splunk connection.
///
/// SECURITY: <see cref="TokenEnvVar"/> is a REFERENCE — the name of an
/// environment variable on the Hub host holding the Splunk token. The value
/// is never stored, never displayed and never enters a sandbox.
/// </summary>
public sealed record SplunkConnection
{
    /// <summary>Splunk management API base URL (e.g. https://splunk.internal:8089).</summary>
    public required string BaseUrl { get; init; }

    /// <summary>Env-var NAME on the Hub host holding the Splunk token.</summary>
    public string? TokenEnvVar { get; init; }
}

/// <summary>
/// Read-only Splunk search operations. Deliberately no write/config surface —
/// SplunkSentinel observes; it never changes anything in Splunk.
/// </summary>
public interface ISplunkSearchClient
{
    /// <summary>
    /// Runs a blocking one-shot search and returns the raw JSON result body
    /// (truncated to a prompt-safe size).
    /// </summary>
    Task<string> OneshotSearchAsync(
        SplunkConnection connection, string query, CancellationToken ct = default);
}

/// <summary>
/// Minimal HTTP implementation against Splunk's REST API
/// (POST /services/search/jobs/oneshot with output_mode=json).
/// </summary>
public sealed class HttpSplunkSearchClient : ISplunkSearchClient
{
    private const int MaxResultChars = 12_000;

    private readonly HttpClient _http;
    private readonly Func<string, string?> _readEnv;

    public HttpSplunkSearchClient(HttpClient http, Func<string, string?>? readEnv = null)
    {
        _http = http;
        _readEnv = readEnv ?? Environment.GetEnvironmentVariable;
    }

    public async Task<string> OneshotSearchAsync(
        SplunkConnection connection, string query, CancellationToken ct = default)
    {
        // Splunk requires searches to start with a command; normalise to "search …".
        var search = query.TrimStart().StartsWith("|", StringComparison.Ordinal)
            || query.TrimStart().StartsWith("search ", StringComparison.OrdinalIgnoreCase)
            ? query
            : "search " + query;

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{connection.BaseUrl.TrimEnd('/')}/services/search/jobs/oneshot");
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["search"] = search,
            ["output_mode"] = "json",
        });

        var token = connection.TokenEnvVar is null ? null : _readEnv(connection.TokenEnvVar);
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return body.Length <= MaxResultChars ? body : body[..MaxResultChars] + "…(truncated)";
    }
}
