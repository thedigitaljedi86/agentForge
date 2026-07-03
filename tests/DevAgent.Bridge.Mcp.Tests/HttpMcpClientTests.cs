namespace DevAgent.Bridge.Mcp.Tests;

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DevAgent.Bridge.Mcp;
using Xunit;

public class HttpMcpClientTests
{
    /// <summary>Scripted JSON-RPC server: answers by method name, records requests.</summary>
    private sealed class FakeRpcHandler : HttpMessageHandler
    {
        public List<JsonObject> Requests { get; } = new();
        public Dictionary<string, JsonObject> ResultsByMethod { get; } = new();
        public Dictionary<string, string> LastHeaders { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            foreach (var header in request.Headers)
            {
                LastHeaders[header.Key] = string.Join(",", header.Value);
            }

            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            Requests.Add(body);

            var method = (string?)body["method"] ?? "";
            if (body["id"] is null)
            {
                // notification
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }

            var result = ResultsByMethod.GetValueOrDefault(method, new JsonObject());
            var payload = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = body["id"]!.DeepClone(),
                ["result"] = result.DeepClone(),
            };

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            response.Headers.TryAddWithoutValidation("Mcp-Session-Id", "session-1");
            return response;
        }
    }

    private static McpServerRegistration Server(string[]? tools = null, string[]? prompts = null) => new()
    {
        Key = "advisories",
        Endpoint = "https://mcp.internal/advisories",
        AllowedTools = tools ?? new[] { "query" },
        AllowedPrompts = prompts ?? new[] { "summarize" },
        AuthHeaderName = "Authorization",
        AuthTokenEnvVar = "MCP_ADVISORIES_TOKEN",
    };

    private static (HttpMcpClient client, FakeRpcHandler handler) NewClient(McpServerRegistration server)
    {
        var handler = new FakeRpcHandler();
        var client = new HttpMcpClient(
            new HttpClient(handler),
            key => key == server.Key ? server : null,
            readEnv: name => name == "MCP_ADVISORIES_TOKEN" ? "Bearer secret-token" : null);
        return (client, handler);
    }

    [Fact]
    public async Task Initializes_before_first_call_and_reuses_the_session()
    {
        var (client, handler) = NewClient(Server());
        handler.ResultsByMethod["tools/list"] = new JsonObject { ["tools"] = new JsonArray() };

        await client.ListToolsAsync("advisories");
        await client.ListToolsAsync("advisories");

        var methods = handler.Requests.Select(r => (string?)r["method"]).ToList();
        Assert.Equal("initialize", methods[0]);
        Assert.Equal("notifications/initialized", methods[1]);
        Assert.Equal("tools/list", methods[2]);
        Assert.Equal("tools/list", methods[3]); // no re-initialize
    }

    [Fact]
    public async Task Auth_header_comes_from_host_environment()
    {
        var (client, handler) = NewClient(Server());
        handler.ResultsByMethod["tools/list"] = new JsonObject { ["tools"] = new JsonArray() };

        await client.ListToolsAsync("advisories");

        Assert.Equal("Bearer secret-token", handler.LastHeaders["Authorization"]);
    }

    [Fact]
    public async Task ListTools_only_surfaces_allowlisted_tools()
    {
        var (client, handler) = NewClient(Server(tools: new[] { "query" }));
        handler.ResultsByMethod["tools/list"] = new JsonObject
        {
            ["tools"] = new JsonArray
            {
                new JsonObject { ["name"] = "query", ["description"] = "Query advisories" },
                new JsonObject { ["name"] = "hidden_admin_tool", ["description"] = "nope" },
            },
        };

        var tools = await client.ListToolsAsync("advisories");

        var tool = Assert.Single(tools);
        Assert.Equal("query", tool.Name);
        Assert.Equal("mcp__advisories__query", tool.WireName);
    }

    [Fact]
    public async Task CallTool_refuses_non_allowlisted_tool_without_contacting_logic()
    {
        var (client, _) = NewClient(Server(tools: new[] { "query" }));

        var result = await client.CallToolAsync("advisories", "hidden_admin_tool", "{}");

        Assert.False(result.Success);
        Assert.Contains("not allowlisted", result.Error);
    }

    [Fact]
    public async Task CallTool_flattens_text_content_and_maps_isError()
    {
        var (client, handler) = NewClient(Server());
        handler.ResultsByMethod["tools/call"] = new JsonObject
        {
            ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "CVE-2026-1234: patched in 3.1.1" } },
            ["isError"] = false,
        };

        var result = await client.CallToolAsync("advisories", "query", """{"package":"Serilog"}""");

        Assert.True(result.Success);
        Assert.Contains("CVE-2026-1234", result.Content);

        // Arguments were forwarded as structured JSON, not interpolated text.
        var call = handler.Requests.Last();
        Assert.Equal("Serilog", (string?)call["params"]!["arguments"]!["package"]);
    }

    [Fact]
    public async Task GetPrompt_returns_flattened_messages_and_respects_allowlist()
    {
        var (client, handler) = NewClient(Server(prompts: new[] { "summarize" }));
        handler.ResultsByMethod["prompts/get"] = new JsonObject
        {
            ["description"] = "Summarize an advisory",
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonObject { ["type"] = "text", ["text"] = "Summarize CVE-2026-1234 for a PR body." },
                },
            },
        };

        var prompt = await client.GetPromptAsync("advisories", "summarize", "{}");
        Assert.Contains("Summarize CVE-2026-1234", prompt.Text);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetPromptAsync("advisories", "not_allowlisted", "{}"));
    }

    [Fact]
    public async Task Unknown_or_disabled_server_fails_closed()
    {
        var (client, _) = NewClient(Server());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListToolsAsync("unknown"));
    }
}
