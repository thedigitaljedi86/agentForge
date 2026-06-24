namespace DevAgent.Bridge.Llm;

/// <summary>
/// The LLM providers a DevAgent coding agent can be configured to use. Each maps
/// to a concrete <see cref="DevAgent.Forge.ILlmClient"/> implementation.
/// </summary>
public enum LlmProvider
{
    /// <summary>Anthropic Claude (Messages API). The default.</summary>
    Claude = 0,

    /// <summary>OpenAI ChatGPT (Chat Completions API).</summary>
    OpenAi = 1,

    /// <summary>Google Gemini (generateContent API).</summary>
    Gemini = 2,
}
