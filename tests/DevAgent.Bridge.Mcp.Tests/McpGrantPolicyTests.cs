namespace DevAgent.Bridge.Mcp.Tests;

using DevAgent.Bridge.Mcp;
using Xunit;

public class McpGrantPolicyTests
{
    private static McpServerRegistration Server(
        string key = "nuget-advisories",
        bool enabled = true,
        string[]? tools = null,
        string[]? prompts = null) => new()
    {
        Key = key,
        Endpoint = "https://mcp.internal/advisories",
        AllowedTools = tools ?? new[] { "query_advisories" },
        AllowedPrompts = prompts ?? new[] { "summarize_advisory" },
        Enabled = enabled,
    };

    private static McpGrant Grant(
        string serverKey = "nuget-advisories",
        string[]? tools = null,
        string[]? prompts = null) => new()
    {
        ServerKey = serverKey,
        Tools = tools ?? new[] { "query_advisories" },
        Prompts = prompts ?? new[] { "summarize_advisory" },
    };

    [Fact]
    public void Tool_allowed_when_registry_and_grant_intersect()
    {
        var policy = new McpGrantPolicy(new[] { Server() }, new[] { Grant() });
        Assert.True(policy.ValidateTool("nuget-advisories", "query_advisories").IsValid);
    }

    [Fact]
    public void Unregistered_server_fails_closed()
    {
        var policy = new McpGrantPolicy(new[] { Server() }, new[] { Grant(serverKey: "other") });
        Assert.False(policy.ValidateTool("other", "query_advisories").IsValid);
    }

    [Fact]
    public void Disabled_server_is_rejected()
    {
        var policy = new McpGrantPolicy(new[] { Server(enabled: false) }, new[] { Grant() });
        Assert.False(policy.ValidateTool("nuget-advisories", "query_advisories").IsValid);
    }

    [Fact]
    public void Tool_outside_registry_allowlist_is_rejected_even_when_granted()
    {
        // The agent's grant claims a tool the ADMIN never allowlisted → deny.
        var policy = new McpGrantPolicy(
            new[] { Server(tools: new[] { "query_advisories" }) },
            new[] { Grant(tools: new[] { "delete_everything" }) });

        Assert.False(policy.ValidateTool("nuget-advisories", "delete_everything").IsValid);
    }

    [Fact]
    public void Tool_in_registry_but_not_granted_is_rejected()
    {
        var policy = new McpGrantPolicy(
            new[] { Server(tools: new[] { "query_advisories", "other_tool" }) },
            new[] { Grant(tools: new[] { "query_advisories" }) });

        Assert.False(policy.ValidateTool("nuget-advisories", "other_tool").IsValid);
    }

    [Fact]
    public void Prompts_are_gated_by_the_same_intersection()
    {
        var policy = new McpGrantPolicy(new[] { Server() }, new[] { Grant() });

        Assert.True(policy.ValidatePrompt("nuget-advisories", "summarize_advisory").IsValid);
        Assert.False(policy.ValidatePrompt("nuget-advisories", "exfiltrate").IsValid);
    }

    [Fact]
    public void EffectiveTools_is_the_intersection()
    {
        var policy = new McpGrantPolicy(
            new[] { Server(tools: new[] { "a", "b" }) },
            new[] { Grant(tools: new[] { "b", "c" }) });

        var effective = policy.EffectiveTools();
        Assert.Equal(("nuget-advisories", "b"), Assert.Single(effective));
    }
}
