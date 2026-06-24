namespace DevAgent.Bridge.Llm;

using System.Net.Http;
using DevAgent.Forge;

/// <summary>
/// Builds the concrete <see cref="ILlmClient"/> for a given provider + model.
/// This is the single composition point that turns an agent's
/// <see cref="LlmClientOptions"/> (its chosen provider and model) into a usable
/// client. The resulting client is then handed to
/// <see cref="CodingAgentFactory.Create"/> as the agent's LLM.
/// </summary>
public static class LlmClientFactory
{
    public static ILlmClient Create(LlmClientOptions options, HttpClient http) => options.Provider switch
    {
        LlmProvider.Claude => new ClaudeLlmClient(http, options),
        LlmProvider.OpenAi => new OpenAiLlmClient(http, options),
        LlmProvider.Gemini => new GeminiLlmClient(http, options),
        _ => throw new LlmClientException($"Unsupported LLM provider '{options.Provider}'."),
    };

    /// <summary>The default model used when an agent does not pin one.</summary>
    public static string DefaultModel(LlmProvider provider) => provider switch
    {
        // Latest Claude Opus — see the Anthropic model catalogue.
        LlmProvider.Claude => "claude-opus-4-8",
        LlmProvider.OpenAi => "gpt-4o",
        LlmProvider.Gemini => "gemini-2.0-flash",
        _ => throw new LlmClientException($"Unsupported LLM provider '{provider}'."),
    };

    public static string DefaultBaseUrl(LlmProvider provider) => provider switch
    {
        LlmProvider.Claude => "https://api.anthropic.com",
        LlmProvider.OpenAi => "https://api.openai.com",
        LlmProvider.Gemini => "https://generativelanguage.googleapis.com",
        _ => throw new LlmClientException($"Unsupported LLM provider '{provider}'."),
    };

    public static string DefaultApiKeyEnvironmentVariable(LlmProvider provider) => provider switch
    {
        LlmProvider.Claude => "ANTHROPIC_API_KEY",
        LlmProvider.OpenAi => "OPENAI_API_KEY",
        LlmProvider.Gemini => "GEMINI_API_KEY",
        _ => throw new LlmClientException($"Unsupported LLM provider '{provider}'."),
    };
}
