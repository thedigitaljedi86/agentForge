namespace DevAgent.Bridge.Llm.Tests;

using System.Net.Http;
using DevAgent.Bridge.Llm;
using Xunit;

public class LlmClientFactoryTests
{
    [Theory]
    [InlineData(LlmProvider.Claude, typeof(ClaudeLlmClient))]
    [InlineData(LlmProvider.OpenAi, typeof(OpenAiLlmClient))]
    [InlineData(LlmProvider.Gemini, typeof(GeminiLlmClient))]
    public void Create_returns_the_client_for_the_provider(LlmProvider provider, Type expected)
    {
        var client = LlmClientFactory.Create(new LlmClientOptions { Provider = provider }, new HttpClient());
        Assert.IsType(expected, client);
    }

    [Theory]
    [InlineData(LlmProvider.Claude, "claude-opus-4-8")]
    [InlineData(LlmProvider.OpenAi, "gpt-4o")]
    [InlineData(LlmProvider.Gemini, "gemini-2.0-flash")]
    public void Default_model_per_provider(LlmProvider provider, string expected)
    {
        Assert.Equal(expected, LlmClientFactory.DefaultModel(provider));
        Assert.Equal(expected, new LlmClientOptions { Provider = provider }.ResolveModel());
    }

    [Fact]
    public void An_agent_can_pin_its_own_model()
    {
        // This is the "specify an LLM model for an agent" requirement: the agent
        // sets Model and the resolved model honours it over the provider default.
        var options = new LlmClientOptions { Provider = LlmProvider.Claude, Model = "claude-sonnet-4-6" };
        Assert.Equal("claude-sonnet-4-6", options.ResolveModel());
    }

    [Fact]
    public void Base_url_falls_back_to_provider_default_and_trims_overrides()
    {
        Assert.Equal("https://api.anthropic.com", new LlmClientOptions { Provider = LlmProvider.Claude }.ResolveBaseUrl());
        Assert.Equal("https://proxy.internal", new LlmClientOptions { BaseUrl = "https://proxy.internal/" }.ResolveBaseUrl());
    }

    [Fact]
    public void Resolve_api_key_prefers_explicit_then_environment_then_throws()
    {
        Assert.Equal("explicit", new LlmClientOptions { ApiKey = "explicit" }.ResolveApiKey());

        var envName = "DEVAGENT_TEST_KEY_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(envName, "from-env");
        try
        {
            var fromEnv = new LlmClientOptions { ApiKeyEnvironmentVariable = envName }.ResolveApiKey();
            Assert.Equal("from-env", fromEnv);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
        }

        var missing = new LlmClientOptions
        {
            Provider = LlmProvider.OpenAi,
            ApiKeyEnvironmentVariable = "DEVAGENT_MISSING_" + Guid.NewGuid().ToString("N"),
        };
        Assert.Throws<LlmClientException>(() => missing.ResolveApiKey());
    }
}
