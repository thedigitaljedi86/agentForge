namespace DevAgent.Bridge.Llm.Tests;

using System.Text.Json.Nodes;
using DevAgent.Bridge.Llm;
using DevAgent.Forge;
using Xunit;

/// <summary>
/// Exercises each provider client's wire format against a captured HTTP request:
/// correct endpoint + auth headers + body, and correct mapping of a provider's
/// tool call (and of a plain text completion) back into an <see cref="LlmDecision"/>.
/// </summary>
public class LlmClientWireTests
{
    private static CodingAgentTask NewTask() => new()
    {
        JobId = "job-1",
        Goal = "Fix the build.",
        WorkspaceRoot = "/work",
    };

    private static LlmClientOptions Options(LlmProvider provider) =>
        new() { Provider = provider, ApiKey = "k" };

    // ---- Claude -----------------------------------------------------------

    [Fact]
    public async Task Claude_maps_tool_use_block_and_sends_required_headers()
    {
        var handler = new CapturingHttpHandler(
            """{"content":[{"type":"text","text":"Let me look."},{"type":"tool_use","id":"toolu_1","name":"read_file","input":{"relativePath":"src/A.cs"}}],"stop_reason":"tool_use"}""");
        var client = new ClaudeLlmClient(handler.Client(), Options(LlmProvider.Claude));

        var decision = await client.GetNextDecisionAsync(NewTask(), Array.Empty<AgentStep>());

        // Headers + endpoint follow the Anthropic Messages API.
        Assert.EndsWith("/v1/messages", handler.LastUri!.AbsolutePath);
        Assert.Equal("k", handler.Header("x-api-key"));
        Assert.Equal("2023-06-01", handler.Header("anthropic-version"));
        Assert.Equal("claude-opus-4-8", handler.LastJson!["model"]!.GetValue<string>());
        Assert.Equal(7, (handler.LastJson["tools"] as JsonArray)!.Count);

        var call = Assert.IsType<ReadFileToolCall>(decision.ToolCall);
        Assert.Equal("src/A.cs", call.RelativePath);
        Assert.Equal("toolu_1", call.ToolCallId);
        Assert.Equal("Let me look.", decision.Reasoning);
        Assert.False(decision.IsComplete);
    }

    [Fact]
    public async Task Claude_text_only_response_is_a_completion()
    {
        var handler = new CapturingHttpHandler(
            """{"content":[{"type":"text","text":"All done."}],"stop_reason":"end_turn"}""");
        var client = new ClaudeLlmClient(handler.Client(), Options(LlmProvider.Claude));

        var decision = await client.GetNextDecisionAsync(NewTask(), Array.Empty<AgentStep>());

        Assert.True(decision.IsComplete);
        Assert.Null(decision.ToolCall);
        Assert.Equal("All done.", decision.Summary);
    }

    [Fact]
    public async Task Claude_replays_history_as_tool_use_and_tool_result_turns()
    {
        var handler = new CapturingHttpHandler(
            """{"content":[{"type":"text","text":"done"}],"stop_reason":"end_turn"}""");
        var client = new ClaudeLlmClient(handler.Client(), Options(LlmProvider.Claude));

        var request = new ReadFileToolCall { RelativePath = "src/A.cs", ToolCallId = "toolu_1" };
        var step = new AgentStep { Request = request, Result = ToolCallResult.Ok(request, "file contents") };

        await client.GetNextDecisionAsync(NewTask(), new[] { step });

        var messages = (handler.LastJson!["messages"] as JsonArray)!;
        Assert.Equal(3, messages.Count); // initial user, assistant tool_use, user tool_result

        var assistant = messages[1] as JsonObject;
        Assert.Equal("assistant", assistant!["role"]!.GetValue<string>());
        var toolUse = (assistant["content"] as JsonArray)![0] as JsonObject;
        Assert.Equal("tool_use", toolUse!["type"]!.GetValue<string>());
        Assert.Equal("toolu_1", toolUse["id"]!.GetValue<string>());

        var toolResult = ((messages[2] as JsonObject)!["content"] as JsonArray)![0] as JsonObject;
        Assert.Equal("tool_result", toolResult!["type"]!.GetValue<string>());
        Assert.Equal("toolu_1", toolResult["tool_use_id"]!.GetValue<string>());
        Assert.Equal("file contents", toolResult["content"]!.GetValue<string>());
    }

    // ---- OpenAI -----------------------------------------------------------

    [Fact]
    public async Task OpenAi_maps_tool_calls_and_sends_bearer_auth()
    {
        var handler = new CapturingHttpHandler(
            """{"choices":[{"message":{"content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"apply_patch","arguments":"{\"relativePath\":\"a.cs\",\"unifiedDiff\":\"d\"}"}}]}}]}""");
        var client = new OpenAiLlmClient(handler.Client(), Options(LlmProvider.OpenAi));

        var decision = await client.GetNextDecisionAsync(NewTask(), Array.Empty<AgentStep>());

        Assert.EndsWith("/v1/chat/completions", handler.LastUri!.AbsolutePath);
        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("k", handler.LastRequest.Headers.Authorization!.Parameter);

        var call = Assert.IsType<ApplyPatchToolCall>(decision.ToolCall);
        Assert.Equal("a.cs", call.RelativePath);
        Assert.Equal("d", call.UnifiedDiff);
        Assert.Equal("call_1", call.ToolCallId);
    }

    [Fact]
    public async Task OpenAi_text_response_is_a_completion()
    {
        var handler = new CapturingHttpHandler("""{"choices":[{"message":{"content":"done"}}]}""");
        var client = new OpenAiLlmClient(handler.Client(), Options(LlmProvider.OpenAi));

        var decision = await client.GetNextDecisionAsync(NewTask(), Array.Empty<AgentStep>());

        Assert.True(decision.IsComplete);
        Assert.Equal("done", decision.Summary);
    }

    // ---- Gemini -----------------------------------------------------------

    [Fact]
    public async Task Gemini_maps_function_call_and_sends_api_key_header()
    {
        var handler = new CapturingHttpHandler(
            """{"candidates":[{"content":{"parts":[{"functionCall":{"name":"git_status","args":{}}}]}}]}""");
        var client = new GeminiLlmClient(handler.Client(), Options(LlmProvider.Gemini));

        var decision = await client.GetNextDecisionAsync(NewTask(), Array.Empty<AgentStep>());

        Assert.Contains("gemini-2.0-flash:generateContent", handler.LastUri!.AbsoluteUri);
        Assert.Equal("k", handler.Header("x-goog-api-key"));
        Assert.IsType<GitStatusToolCall>(decision.ToolCall);
    }

    [Fact]
    public async Task Gemini_text_response_is_a_completion()
    {
        var handler = new CapturingHttpHandler(
            """{"candidates":[{"content":{"parts":[{"text":"ok"}]}}]}""");
        var client = new GeminiLlmClient(handler.Client(), Options(LlmProvider.Gemini));

        var decision = await client.GetNextDecisionAsync(NewTask(), Array.Empty<AgentStep>());

        Assert.True(decision.IsComplete);
        Assert.Equal("ok", decision.Summary);
    }

    [Fact]
    public async Task Non_success_status_throws_a_descriptive_exception()
    {
        var handler = new ErrorHttpHandler(System.Net.HttpStatusCode.Unauthorized, "bad key");
        var client = new ClaudeLlmClient(new System.Net.Http.HttpClient(handler), Options(LlmProvider.Claude));

        var ex = await Assert.ThrowsAsync<LlmClientException>(
            () => client.GetNextDecisionAsync(NewTask(), Array.Empty<AgentStep>()));
        Assert.Contains("401", ex.Message);
    }

    private sealed class ErrorHttpHandler : System.Net.Http.HttpMessageHandler
    {
        private readonly System.Net.HttpStatusCode _status;
        private readonly string _body;

        public ErrorHttpHandler(System.Net.HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new System.Net.Http.HttpResponseMessage(_status)
            {
                Content = new System.Net.Http.StringContent(_body),
            });
    }
}
