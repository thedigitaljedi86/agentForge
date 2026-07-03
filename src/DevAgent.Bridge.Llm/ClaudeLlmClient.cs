namespace DevAgent.Bridge.Llm;

using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevAgent.Forge;

/// <summary>
/// <see cref="ILlmClient"/> backed by the Anthropic Claude Messages API
/// (POST /v1/messages). The default model is the latest Claude Opus.
///
/// Wire format reference: Anthropic Messages API. Required headers are
/// 'x-api-key' and 'anthropic-version'. Tool calls arrive as 'tool_use' content
/// blocks; the model is fed tool outputs as 'tool_result' blocks on the next turn.
/// </summary>
public sealed class ClaudeLlmClient : ILlmClient
{
    private const string AnthropicVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly LlmClientOptions _options;

    public ClaudeLlmClient(HttpClient http, LlmClientOptions options)
    {
        _http = http;
        _options = options;
    }

    public async Task<LlmDecision> GetNextDecisionAsync(
        CodingAgentTask task,
        IReadOnlyList<AgentStep> history,
        CancellationToken cancellationToken = default)
    {
        var messages = new JsonArray
        {
            UserText(LlmConversation.InitialUserMessage(task)),
        };

        foreach (var step in history)
        {
            messages.Add(AssistantToolUse(step.Request));
            messages.Add(ToolResult(step.Request.ToolCallId, LlmConversation.ResultText(step.Result)));
        }

        var body = new JsonObject
        {
            ["model"] = _options.ResolveModel(),
            ["max_tokens"] = _options.MaxOutputTokens,
            ["system"] = LlmConversation.SystemPrompt(task),
            ["tools"] = LlmToolCatalog.AnthropicTools(_options.AdditionalTools),
            ["messages"] = messages,
        };

        var apiKey = _options.ResolveApiKey();
        var url = _options.ResolveBaseUrl() + "/v1/messages";

        var json = await LlmHttp.PostJsonAsync(_http, url, body, request =>
        {
            request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
        }, "Claude", cancellationToken).ConfigureAwait(false);

        return Interpret(json);
    }

    private static LlmDecision Interpret(JsonObject json)
    {
        var content = json["content"] as JsonArray;
        var text = new System.Text.StringBuilder();

        if (content is not null)
        {
            foreach (var block in content)
            {
                if (block is not JsonObject obj)
                {
                    continue;
                }

                var type = obj["type"]?.GetValue<string>();
                if (type == "tool_use")
                {
                    var name = obj["name"]?.GetValue<string>() ?? string.Empty;
                    var id = obj["id"]?.GetValue<string>();
                    var input = obj["input"] as JsonObject;
                    var call = LlmToolCatalog.Parse(name, input, id);
                    if (call is not null)
                    {
                        return new LlmDecision
                        {
                            ToolCall = call,
                            Reasoning = text.Length > 0 ? text.ToString().Trim() : null,
                        };
                    }
                }
                else if (type == "text")
                {
                    text.Append(obj["text"]?.GetValue<string>());
                }
            }
        }

        var summary = text.ToString().Trim();
        return new LlmDecision
        {
            IsComplete = true,
            Summary = summary.Length > 0 ? summary : null,
        };
    }

    private static JsonObject UserText(string text) => new()
    {
        ["role"] = "user",
        ["content"] = text,
    };

    private static JsonObject AssistantToolUse(ToolCallRequest request) => new()
    {
        ["role"] = "assistant",
        ["content"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "tool_use",
                ["id"] = request.ToolCallId,
                ["name"] = request.ToolName,
                ["input"] = LlmToolCatalog.Arguments(request),
            },
        },
    };

    private static JsonObject ToolResult(string toolUseId, string output) => new()
    {
        ["role"] = "user",
        ["content"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "tool_result",
                ["tool_use_id"] = toolUseId,
                ["content"] = output,
            },
        },
    };
}
