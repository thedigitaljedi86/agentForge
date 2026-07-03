namespace DevAgent.Bridge.Confluence;

using System.Net.Http.Json;
using System.Text.Json;

/// <summary>
/// An administrator-configured Confluence connection.
///
/// SECURITY: <see cref="TokenEnvVar"/> is a REFERENCE — the name of an
/// environment variable on the Hub host holding the Confluence token. The
/// value is never stored, never displayed and never enters a sandbox.
/// </summary>
public sealed record ConfluenceConnection
{
    /// <summary>Confluence base URL (e.g. https://confluence.internal or https://x.atlassian.net/wiki).</summary>
    public required string BaseUrl { get; init; }

    /// <summary>Space key pages are managed in.</summary>
    public required string SpaceKey { get; init; }

    /// <summary>Env-var NAME on the Hub host holding the Confluence token.</summary>
    public string? TokenEnvVar { get; init; }
}

/// <summary>A Confluence page reference.</summary>
public sealed record ConfluencePage(string Id, string Title, int Version);

/// <summary>
/// Minimal Confluence page operations for documentation sync: find a page by
/// title and create/update page content. No delete, no permissions surface.
/// </summary>
public interface IConfluenceClient
{
    Task<ConfluencePage?> FindPageAsync(
        ConfluenceConnection connection, string title, CancellationToken ct = default);

    /// <summary>Creates the page, or updates it (bumping the version) when it exists.</summary>
    Task<ConfluencePage> UpsertPageAsync(
        ConfluenceConnection connection, string title, string storageHtml, CancellationToken ct = default);
}

/// <summary>Minimal HTTP implementation against the Confluence REST API (v1 content endpoints).</summary>
public sealed class HttpConfluenceClient : IConfluenceClient
{
    private readonly HttpClient _http;
    private readonly Func<string, string?> _readEnv;

    public HttpConfluenceClient(HttpClient http, Func<string, string?>? readEnv = null)
    {
        _http = http;
        _readEnv = readEnv ?? Environment.GetEnvironmentVariable;
    }

    public async Task<ConfluencePage?> FindPageAsync(
        ConfluenceConnection connection, string title, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{connection.BaseUrl.TrimEnd('/')}/rest/api/content" +
            $"?spaceKey={Uri.EscapeDataString(connection.SpaceKey)}" +
            $"&title={Uri.EscapeDataString(title)}&expand=version");
        Authorize(request, connection);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var results = doc.RootElement.GetProperty("results");
        if (results.GetArrayLength() == 0)
        {
            return null;
        }

        var first = results[0];
        return new ConfluencePage(
            first.GetProperty("id").GetString()!,
            first.GetProperty("title").GetString()!,
            first.GetProperty("version").GetProperty("number").GetInt32());
    }

    public async Task<ConfluencePage> UpsertPageAsync(
        ConfluenceConnection connection, string title, string storageHtml, CancellationToken ct = default)
    {
        var existing = await FindPageAsync(connection, title, ct).ConfigureAwait(false);
        var baseUrl = connection.BaseUrl.TrimEnd('/');

        HttpRequestMessage request;
        if (existing is null)
        {
            request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/rest/api/content");
            request.Content = JsonContent.Create(new
            {
                type = "page",
                title,
                space = new { key = connection.SpaceKey },
                body = new { storage = new { value = storageHtml, representation = "storage" } },
            });
        }
        else
        {
            request = new HttpRequestMessage(HttpMethod.Put, $"{baseUrl}/rest/api/content/{existing.Id}");
            request.Content = JsonContent.Create(new
            {
                type = "page",
                title,
                version = new { number = existing.Version + 1 },
                body = new { storage = new { value = storageHtml, representation = "storage" } },
            });
        }

        using (request)
        {
            Authorize(request, connection);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return new ConfluencePage(
                doc.RootElement.GetProperty("id").GetString()!,
                doc.RootElement.GetProperty("title").GetString()!,
                doc.RootElement.GetProperty("version").GetProperty("number").GetInt32());
        }
    }

    private void Authorize(HttpRequestMessage request, ConfluenceConnection connection)
    {
        var token = connection.TokenEnvVar is null ? null : _readEnv(connection.TokenEnvVar);
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }
    }
}
