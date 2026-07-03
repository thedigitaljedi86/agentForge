namespace DevAgent.Bridge.Mcp;

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Minimal MCP client over streamable HTTP (JSON-RPC 2.0): initialize,
/// tools/list, tools/call, prompts/list, prompts/get. Nothing else — no
/// resources, no sampling, no arbitrary passthrough.
///
/// SECURITY: This client only ever connects to endpoints from the registered
/// server set given at construction (resolved from the admin store). The auth
/// header value is read from the HOST environment via the registration's
/// AuthTokenEnvVar — it is never persisted or forwarded to callers.
/// stdio-launched local servers are deliberately unsupported (that would mean
/// process execution; a later, separately gated feature).
/// </summary>
public sealed class HttpMcpClient : IMcpClient
{
    private const string ProtocolVersion = "2025-03-26";

    private readonly HttpClient _http;
    private readonly Func<string, McpServerRegistration?> _resolveServer;
    private readonly Func<string, string?> _readEnv;
    private readonly Dictionary<string, string?> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private int _nextId;

    public HttpMcpClient(
        HttpClient http,
        Func<string, McpServerRegistration?> resolveServer,
        Func<string, string?>? readEnv = null)
    {
        _http = http;
        _resolveServer = resolveServer;
        _readEnv = readEnv ?? Environment.GetEnvironmentVariable;
    }

    public async Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(string serverKey, CancellationToken ct = default)
    {
        var server = RequireServer(serverKey);
        var result = await RpcAsync(server, "tools/list", new JsonObject(), ct).ConfigureAwait(false);

        var tools = new List<McpToolDescriptor>();
        foreach (var node in result?["tools"] as JsonArray ?? new JsonArray())
        {
            var name = (string?)node?["name"];
            if (name is null)
            {
                continue;
            }

            // Only surface tools the registration allowlists — everything else
            // is invisible to the rest of the platform.
            if (!server.AllowedTools.Contains(name, StringComparer.Ordinal))
            {
                continue;
            }

            tools.Add(new McpToolDescriptor
            {
                ServerKey = server.Key,
                Name = name,
                Description = (string?)node?["description"] ?? string.Empty,
                InputSchemaJson = node?["inputSchema"]?.ToJsonString() ?? """{"type":"object"}""",
            });
        }

        return tools;
    }

    public async Task<McpCallResult> CallToolAsync(string serverKey, string tool, string argumentsJson, CancellationToken ct = default)
    {
        var server = RequireServer(serverKey);

        // Defence in depth — the grant policy has already validated, but this
        // client refuses non-allowlisted tools even if called directly.
        if (!server.AllowedTools.Contains(tool, StringComparer.Ordinal))
        {
            return new McpCallResult { Success = false, Error = $"Tool '{tool}' is not allowlisted on server '{serverKey}'." };
        }

        var @params = new JsonObject
        {
            ["name"] = tool,
            ["arguments"] = ParseArguments(argumentsJson),
        };

        var result = await RpcAsync(server, "tools/call", @params, ct).ConfigureAwait(false);

        var isError = (bool?)result?["isError"] ?? false;
        var text = FlattenContent(result?["content"] as JsonArray);

        return new McpCallResult
        {
            Success = !isError,
            Content = text,
            Error = isError ? (text.Length > 0 ? text : "MCP tool reported an error.") : null,
        };
    }

    public async Task<IReadOnlyList<McpPromptDescriptor>> ListPromptsAsync(string serverKey, CancellationToken ct = default)
    {
        var server = RequireServer(serverKey);
        var result = await RpcAsync(server, "prompts/list", new JsonObject(), ct).ConfigureAwait(false);

        var prompts = new List<McpPromptDescriptor>();
        foreach (var node in result?["prompts"] as JsonArray ?? new JsonArray())
        {
            var name = (string?)node?["name"];
            if (name is null || !server.AllowedPrompts.Contains(name, StringComparer.Ordinal))
            {
                continue;
            }

            var args = new List<McpPromptArgument>();
            foreach (var arg in node?["arguments"] as JsonArray ?? new JsonArray())
            {
                args.Add(new McpPromptArgument(
                    (string?)arg?["name"] ?? string.Empty,
                    (string?)arg?["description"] ?? string.Empty,
                    (bool?)arg?["required"] ?? false));
            }

            prompts.Add(new McpPromptDescriptor
            {
                ServerKey = server.Key,
                Name = name,
                Description = (string?)node?["description"] ?? string.Empty,
                Arguments = args,
            });
        }

        return prompts;
    }

    public async Task<McpPromptResult> GetPromptAsync(string serverKey, string promptName, string argumentsJson, CancellationToken ct = default)
    {
        var server = RequireServer(serverKey);

        if (!server.AllowedPrompts.Contains(promptName, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"Prompt '{promptName}' is not allowlisted on server '{serverKey}'.");
        }

        var @params = new JsonObject
        {
            ["name"] = promptName,
            ["arguments"] = ParseArguments(argumentsJson),
        };

        var result = await RpcAsync(server, "prompts/get", @params, ct).ConfigureAwait(false);

        var sb = new StringBuilder();
        foreach (var message in result?["messages"] as JsonArray ?? new JsonArray())
        {
            var content = message?["content"];
            if (content is JsonObject single)
            {
                AppendContentItem(sb, single);
            }
            else if (content is JsonArray many)
            {
                foreach (var item in many)
                {
                    AppendContentItem(sb, item as JsonObject);
                }
            }
        }

        return new McpPromptResult
        {
            Description = (string?)result?["description"],
            Text = sb.ToString().Trim(),
        };
    }

    // ---- JSON-RPC over streamable HTTP ----

    private async Task<JsonObject?> RpcAsync(McpServerRegistration server, string method, JsonObject @params, CancellationToken ct)
    {
        await EnsureInitializedAsync(server, ct).ConfigureAwait(false);
        return await SendAsync(server, method, @params, ct).ConfigureAwait(false);
    }

    private async Task EnsureInitializedAsync(McpServerRegistration server, CancellationToken ct)
    {
        if (_sessions.ContainsKey(server.Key))
        {
            return;
        }

        _sessions[server.Key] = null; // reserve; replaced by the session id below

        await SendAsync(server, "initialize", new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "DevAgent", ["version"] = "1.0" },
        }, ct).ConfigureAwait(false);

        await SendNotificationAsync(server, "notifications/initialized", ct).ConfigureAwait(false);
    }

    private async Task<JsonObject?> SendAsync(McpServerRegistration server, string method, JsonObject @params, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = @params,
        };

        using var response = await PostAsync(server, body, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // Capture the session id issued during initialize.
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var values))
        {
            _sessions[server.Key] = values.FirstOrDefault();
        }

        var payload = await ReadJsonRpcPayloadAsync(response, id, ct).ConfigureAwait(false);

        if (payload?["error"] is JsonObject error)
        {
            throw new InvalidOperationException(
                $"MCP server '{server.Key}' returned error {(string?)error["code"] ?? "?"}: {(string?)error["message"]}");
        }

        return payload?["result"] as JsonObject;
    }

    private async Task SendNotificationAsync(McpServerRegistration server, string method, CancellationToken ct)
    {
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        using var response = await PostAsync(server, body, ct).ConfigureAwait(false);
        // Notifications expect no payload; 202/200/204 are all fine.
    }

    private async Task<HttpResponseMessage> PostAsync(McpServerRegistration server, JsonObject body, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, server.Endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        if (_sessions.TryGetValue(server.Key, out var session) && !string.IsNullOrEmpty(session))
        {
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", session);
        }

        // Auth header value comes from the gateway host's environment only.
        if (!string.IsNullOrEmpty(server.AuthHeaderName) && !string.IsNullOrEmpty(server.AuthTokenEnvVar))
        {
            var secret = _readEnv(server.AuthTokenEnvVar!);
            if (!string.IsNullOrEmpty(secret))
            {
                request.Headers.TryAddWithoutValidation(server.AuthHeaderName!, secret);
            }
        }

        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    /// <summary>Reads a JSON or SSE response and returns the JSON-RPC message matching <paramref name="id"/>.</summary>
    private static async Task<JsonObject?> ReadJsonRpcPayloadAsync(HttpResponseMessage response, int id, CancellationToken ct)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "application/json";

        if (!mediaType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text) as JsonObject;
        }

        // SSE: scan data: lines for the response with our id.
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var json = line[5..].Trim();
            if (json.Length == 0)
            {
                continue;
            }

            JsonObject? node;
            try
            {
                node = JsonNode.Parse(json) as JsonObject;
            }
            catch (JsonException)
            {
                continue;
            }

            if (node?["id"] is JsonValue value && value.TryGetValue<int>(out var messageId) && messageId == id)
            {
                return node;
            }
        }

        return null;
    }

    private McpServerRegistration RequireServer(string serverKey)
    {
        var server = _resolveServer(serverKey);
        if (server is null || !server.Enabled)
        {
            throw new InvalidOperationException($"MCP server '{serverKey}' is not registered or is disabled.");
        }

        return server;
    }

    private static JsonNode ParseArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(argumentsJson) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static string FlattenContent(JsonArray? content)
    {
        if (content is null)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var item in content)
        {
            AppendContentItem(sb, item as JsonObject);
        }

        return sb.ToString().Trim();
    }

    private static void AppendContentItem(StringBuilder sb, JsonObject? item)
    {
        if (item is null)
        {
            return;
        }

        var type = (string?)item["type"];
        if (type == "text")
        {
            sb.AppendLine((string?)item["text"] ?? string.Empty);
        }
        else if (type is not null)
        {
            // Non-text content (images, resources) is summarised, not forwarded.
            sb.AppendLine($"[unsupported MCP content type: {type}]");
        }
    }
}
