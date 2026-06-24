namespace DevAgent.Bridge.Llm;

using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using DevAgent.Forge;

/// <summary>
/// <see cref="ILlmClient"/> backed by the OpenAI Chat Completions API
/// (POST /v1/chat/completions). Tool calls arrive as 'tool_calls' on the
/// assistant message, with arguments encoded as a JSON string; tool outputs are
/// fed back as messages with role 'tool'.
/// </summary>
public sealed class OpenAiLlmClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly LlmClientOptions _options;

    public OpenAiLlmClient(HttpClient http, LlmClientOptions options)
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
            new JsonObject { ["role"] = "system", ["content"] = LlmConversation.SystemPrompt(task) },
            new JsonObject { ["role"] = "user", ["content"] = LlmConversation.InitialUserMessage(task) },
        };

        foreach (var step in history)
        {
            messages.Add(AssistantToolCall(step.Request));
            messages.Add(new JsonObject
            {
                ["role"] = "tool",
                ["tool_call_id"] = step.Request.ToolCallId,
                ["content"] = LlmConversation.ResultText(step.Result),
            });
        }

        var body = new JsonObject
        {
            ["model"] = _options.ResolveModel(),
            ["max_tokens"] = _options.MaxOutputTokens,
            ["tools"] = LlmToolCatalog.OpenAiTools(),
            ["tool_choice"] = "auto",
            ["messages"] = messages,
        };

        var apiKey = _options.ResolveApiKey();
        var url = _options.ResolveBaseUrl() + "/v1/chat/completions";

        var json = await LlmHttp.PostJsonAsync(_http, url, body, request =>
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }, "OpenAI", cancellationToken).ConfigureAwait(false);

        return Interpret(json);
    }

    private static LlmDecision Interpret(JsonObject json)
    {
        var message = (json["choices"] as JsonArray)?.FirstOrDefault() is JsonObject choice
            ? choice["message"] as JsonObject
            : null;

        if (message is null)
        {
            return new LlmDecision { IsComplete = true, Summary = null };
        }

        var reasoning = message["content"]?.GetValue<string>();

        if (message["tool_calls"] is JsonArray toolCalls && toolCalls.FirstOrDefault() is JsonObject toolCall)
        {
            var function = toolCall["function"] as JsonObject;
            var name = function?["name"]?.GetValue<string>() ?? string.Empty;
            var id = toolCall["id"]?.GetValue<string>();
            var args = ParseArguments(function?["arguments"]?.GetValue<string>());
            var call = LlmToolCatalog.Parse(name, args, id);
            if (call is not null)
            {
                return new LlmDecision
                {
                    ToolCall = call,
                    Reasoning = string.IsNullOrWhiteSpace(reasoning) ? null : reasoning,
                };
            }
        }

        return new LlmDecision
        {
            IsComplete = true,
            Summary = string.IsNullOrWhiteSpace(reasoning) ? null : reasoning,
        };
    }

    private static JsonObject? ParseArguments(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(argumentsJson) as JsonObject ?? new JsonObject();
        }
        catch
        {
            return new JsonObject();
        }
    }

    private static JsonObject AssistantToolCall(ToolCallRequest request) => new()
    {
        ["role"] = "assistant",
        ["tool_calls"] = new JsonArray
        {
            new JsonObject
            {
                ["id"] = request.ToolCallId,
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = request.ToolName,
                    ["arguments"] = LlmToolCatalog.Arguments(request).ToJsonString(),
                },
            },
        },
    };
}
