namespace DevAgent.Bridge.Llm;

/// <summary>
/// Per-agent LLM configuration. An "agent" specifies WHICH provider and WHICH
/// model it wants by binding this options object (e.g. from a configuration
/// section). The model is therefore selectable per agent, not hard-coded.
///
/// SECURITY: API keys are read from the environment by default and never from
/// the caller-facing job surface. The model and provider are operator config.
/// </summary>
public sealed class LlmClientOptions
{
    public const string SectionName = "Llm";

    /// <summary>Which provider this agent uses. Defaults to Claude.</summary>
    public LlmProvider Provider { get; set; } = LlmProvider.Claude;

    /// <summary>
    /// The model id for this agent. When null/empty the provider default is used
    /// (see <see cref="LlmClientFactory.DefaultModel"/>). This is the knob that
    /// lets each agent pick its own model.
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// Explicit API key. Prefer leaving this null and supplying the key via the
    /// environment variable named by <see cref="ApiKeyEnvironmentVariable"/>.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Environment variable that holds the API key. Defaults to the provider's
    /// conventional variable when null (ANTHROPIC_API_KEY / OPENAI_API_KEY /
    /// GEMINI_API_KEY).
    /// </summary>
    public string? ApiKeyEnvironmentVariable { get; set; }

    /// <summary>Optional base-URL override (e.g. for a proxy or gateway).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Hard cap on tokens the model may emit per decision.</summary>
    public int MaxOutputTokens { get; set; } = 4096;

    /// <summary>The model to use, falling back to the provider default.</summary>
    public string ResolveModel() =>
        string.IsNullOrWhiteSpace(Model) ? LlmClientFactory.DefaultModel(Provider) : Model!;

    /// <summary>The base URL to use, falling back to the provider default.</summary>
    public string ResolveBaseUrl() =>
        string.IsNullOrWhiteSpace(BaseUrl) ? LlmClientFactory.DefaultBaseUrl(Provider) : BaseUrl!.TrimEnd('/');

    /// <summary>
    /// Resolve the API key from explicit config or the environment. Throws when
    /// no key is available so a misconfiguration fails loudly, not silently.
    /// </summary>
    public string ResolveApiKey()
    {
        if (!string.IsNullOrWhiteSpace(ApiKey))
        {
            return ApiKey!;
        }

        var envName = string.IsNullOrWhiteSpace(ApiKeyEnvironmentVariable)
            ? LlmClientFactory.DefaultApiKeyEnvironmentVariable(Provider)
            : ApiKeyEnvironmentVariable!;

        var fromEnv = Environment.GetEnvironmentVariable(envName);
        if (string.IsNullOrWhiteSpace(fromEnv))
        {
            throw new LlmClientException(
                $"No API key for provider '{Provider}'. Set the '{envName}' environment variable " +
                "or supply LlmClientOptions.ApiKey.");
        }

        return fromEnv;
    }
}
