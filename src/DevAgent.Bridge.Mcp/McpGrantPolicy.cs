namespace DevAgent.Bridge.Mcp;

using DevAgent.Contracts.Validation;

/// <summary>
/// The MCP access gate: a tool/prompt may be used only when BOTH the server
/// registration allows it AND the agent's grant includes it (intersection).
///
/// SECURITY: This mirrors the platform's other allowlists — the registry is
/// administrator intent ("this server may ever expose these"), the grant is
/// per-agent scope ("this agent may use these"). A skill or model can never
/// widen either set; an unknown server key fails closed.
/// </summary>
public sealed class McpGrantPolicy
{
    private readonly IReadOnlyDictionary<string, McpServerRegistration> _servers;
    private readonly IReadOnlyDictionary<string, McpGrant> _grants;

    public McpGrantPolicy(
        IEnumerable<McpServerRegistration> servers,
        IEnumerable<McpGrant> grants)
    {
        _servers = servers.ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);
        _grants = grants.ToDictionary(g => g.ServerKey, StringComparer.OrdinalIgnoreCase);
    }

    public ValidationResult ValidateTool(string serverKey, string tool) =>
        Validate(serverKey, tool, r => r.AllowedTools, g => g.Tools, "tool");

    public ValidationResult ValidatePrompt(string serverKey, string prompt) =>
        Validate(serverKey, prompt, r => r.AllowedPrompts, g => g.Prompts, "prompt");

    /// <summary>The tools this policy actually permits, per granted server.</summary>
    public IReadOnlyList<(string ServerKey, string Tool)> EffectiveTools()
    {
        var result = new List<(string, string)>();
        foreach (var grant in _grants.Values)
        {
            if (!_servers.TryGetValue(grant.ServerKey, out var server) || !server.Enabled)
            {
                continue;
            }

            foreach (var tool in grant.Tools)
            {
                if (server.AllowedTools.Contains(tool, StringComparer.Ordinal))
                {
                    result.Add((server.Key, tool));
                }
            }
        }

        return result;
    }

    private ValidationResult Validate(
        string serverKey,
        string item,
        Func<McpServerRegistration, IReadOnlyList<string>> registryList,
        Func<McpGrant, IReadOnlyList<string>> grantList,
        string kind)
    {
        if (string.IsNullOrWhiteSpace(serverKey) || string.IsNullOrWhiteSpace(item))
        {
            return ValidationResult.Fail($"MCP server key and {kind} name are required.");
        }

        if (!_servers.TryGetValue(serverKey, out var server))
        {
            return ValidationResult.Fail($"MCP server '{serverKey}' is not registered.");
        }

        if (!server.Enabled)
        {
            return ValidationResult.Fail($"MCP server '{serverKey}' is disabled.");
        }

        if (!registryList(server).Contains(item, StringComparer.Ordinal))
        {
            return ValidationResult.Fail($"MCP {kind} '{item}' is not allowlisted on server '{serverKey}'.");
        }

        if (!_grants.TryGetValue(serverKey, out var grant)
            || !grantList(grant).Contains(item, StringComparer.Ordinal))
        {
            return ValidationResult.Fail($"The agent has no grant for MCP {kind} '{item}' on server '{serverKey}'.");
        }

        return ValidationResult.Success;
    }
}
