namespace DevAgent.Bridge.Mcp;

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// The SANDBOX-side MCP client. It never talks to an MCP server — it talks to
/// the Runner's MCP gateway with a short-lived, per-job bearer token. The
/// gateway holds the server credentials and re-validates every call against
/// the registry and the job's grants (defence in depth on both sides of the
/// sandbox boundary).
/// </summary>
public sealed class GatewayMcpClient : IMcpClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly string _gatewayBaseUrl;
    private readonly string _jobToken;

    public GatewayMcpClient(HttpClient http, string gatewayBaseUrl, string jobToken)
    {
        _http = http;
        _gatewayBaseUrl = gatewayBaseUrl.TrimEnd('/');
        _jobToken = jobToken;
    }

    public async Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(string serverKey, CancellationToken ct = default)
        => await PostAsync<List<McpToolDescriptor>>("/mcp/tools/list", new { serverKey }, ct).ConfigureAwait(false)
           ?? new List<McpToolDescriptor>();

    public async Task<McpCallResult> CallToolAsync(string serverKey, string tool, string argumentsJson, CancellationToken ct = default)
        => await PostAsync<McpCallResult>("/mcp/tools/call", new { serverKey, tool, argumentsJson }, ct).ConfigureAwait(false)
           ?? new McpCallResult { Success = false, Error = "The MCP gateway returned an empty response." };

    public async Task<IReadOnlyList<McpPromptDescriptor>> ListPromptsAsync(string serverKey, CancellationToken ct = default)
        => await PostAsync<List<McpPromptDescriptor>>("/mcp/prompts/list", new { serverKey }, ct).ConfigureAwait(false)
           ?? new List<McpPromptDescriptor>();

    public async Task<McpPromptResult> GetPromptAsync(string serverKey, string promptName, string argumentsJson, CancellationToken ct = default)
        => await PostAsync<McpPromptResult>("/mcp/prompts/get", new { serverKey, promptName, argumentsJson }, ct).ConfigureAwait(false)
           ?? new McpPromptResult { Text = string.Empty };

    private async Task<T?> PostAsync<T>(string path, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _gatewayBaseUrl + path)
        {
            Content = JsonContent.Create(body, options: Json),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jobToken);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false);
    }
}
