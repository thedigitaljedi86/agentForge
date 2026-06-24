namespace DevAgent.Bridge.Llm;

using System.Net.Http;
using System.Text.Json.Nodes;
using DevAgent.Forge;

/// <summary>
/// <see cref="ILlmClient"/> backed by the Google Gemini generateContent API
/// (POST /v1beta/models/{model}:generateContent). Tool calls arrive as
/// 'functionCall' parts; outputs are fed back as 'functionResponse' parts. Gemini
/// function responses are matched by tool NAME (not id), which is sufficient for
/// this strictly sequential, one-tool-per-turn agent loop.
/// </summary>
public sealed class GeminiLlmClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly LlmClientOptions _options;

    public GeminiLlmClient(HttpClient http, LlmClientOptions options)
    {
        _http = http;
        _options = options;
    }

    public async Task<LlmDecision> GetNextDecisionAsync(
        CodingAgentTask task,
        IReadOnlyList<AgentStep> history,
        CancellationToken cancellationToken = default)
    {
        var contents = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray { new JsonObject { ["text"] = LlmConversation.InitialUserMessage(task) } },
            },
        };

        foreach (var step in history)
        {
            contents.Add(new JsonObject
            {
                ["role"] = "model",
                ["parts"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["functionCall"] = new JsonObject
                        {
                            ["name"] = step.Request.ToolName,
                            ["args"] = LlmToolCatalog.Arguments(step.Request),
                        },
                    },
                },
            });
            contents.Add(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["functionResponse"] = new JsonObject
                        {
                            ["name"] = step.Request.ToolName,
                            ["response"] = new JsonObject { ["result"] = LlmConversation.ResultText(step.Result) },
                        },
                    },
                },
            });
        }

        var body = new JsonObject
        {
            ["system_instruction"] = new JsonObject
            {
                ["parts"] = new JsonArray { new JsonObject { ["text"] = LlmConversation.SystemPrompt(task) } },
            },
            ["tools"] = LlmToolCatalog.GeminiTools(),
            ["contents"] = contents,
        };

        var apiKey = _options.ResolveApiKey();
        var model = _options.ResolveModel();
        var url = $"{_options.ResolveBaseUrl()}/v1beta/models/{model}:generateContent";

        var json = await LlmHttp.PostJsonAsync(_http, url, body, request =>
        {
            request.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
        }, "Gemini", cancellationToken).ConfigureAwait(false);

        return Interpret(json);
    }

    private static LlmDecision Interpret(JsonObject json)
    {
        var parts = ((json["candidates"] as JsonArray)?.FirstOrDefault() as JsonObject)
            ?["content"] as JsonObject;
        var partArray = parts?["parts"] as JsonArray;

        var text = new System.Text.StringBuilder();

        if (partArray is not null)
        {
            foreach (var part in partArray)
            {
                if (part is not JsonObject obj)
                {
                    continue;
                }

                if (obj["functionCall"] is JsonObject functionCall)
                {
                    var name = functionCall["name"]?.GetValue<string>() ?? string.Empty;
                    var args = functionCall["args"] as JsonObject;
                    var call = LlmToolCatalog.Parse(name, args);
                    if (call is not null)
                    {
                        return new LlmDecision
                        {
                            ToolCall = call,
                            Reasoning = text.Length > 0 ? text.ToString().Trim() : null,
                        };
                    }
                }
                else if (obj["text"] is not null)
                {
                    text.Append(obj["text"]!.GetValue<string>());
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
}
