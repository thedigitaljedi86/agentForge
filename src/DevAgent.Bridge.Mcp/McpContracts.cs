namespace DevAgent.Bridge.Mcp;

/// <summary>
/// An administrator-registered MCP server. Registrations live in the platform
/// store and are referenced BY KEY everywhere — an agent, a skill or an API
/// caller can never supply an endpoint URL.
///
/// SECURITY: <see cref="AuthTokenEnvVar"/> is a reference to a secret in the
/// GATEWAY host's environment. The secret itself is never stored in the
/// database, never shown in the admin UI and never enters a sandbox.
/// </summary>
public sealed record McpServerRegistration
{
    public required string Key { get; init; }
    public string Name { get; init; } = string.Empty;

    /// <summary>Streamable-HTTP endpoint of the MCP server.</summary>
    public required string Endpoint { get; init; }

    /// <summary>HTTP header used for authentication (e.g. "Authorization"). Optional.</summary>
    public string? AuthHeaderName { get; init; }

    /// <summary>Environment variable ON THE GATEWAY holding the header value.</summary>
    public string? AuthTokenEnvVar { get; init; }

    /// <summary>Tools of this server the platform may expose at all.</summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = Array.Empty<string>();

    /// <summary>Prompts of this server the platform may expose at all.</summary>
    public IReadOnlyList<string> AllowedPrompts { get; init; } = Array.Empty<string>();

    public bool Enabled { get; init; } = true;
}

/// <summary>An agent's grant to use specific tools/prompts of one registered server.</summary>
public sealed record McpGrant
{
    public required string ServerKey { get; init; }
    public IReadOnlyList<string> Tools { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Prompts { get; init; } = Array.Empty<string>();
}

/// <summary>A tool advertised by an MCP server (from tools/list).</summary>
public sealed record McpToolDescriptor
{
    public required string ServerKey { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = string.Empty;

    /// <summary>The tool's JSON-schema "inputSchema", as raw JSON.</summary>
    public string InputSchemaJson { get; init; } = """{"type":"object"}""";

    /// <summary>The wire name the model sees: mcp__{serverKey}__{tool}.</summary>
    public string WireName => $"mcp__{ServerKey}__{Name}";
}

/// <summary>A prompt advertised by an MCP server (from prompts/list).</summary>
public sealed record McpPromptDescriptor
{
    public required string ServerKey { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = string.Empty;
    public IReadOnlyList<McpPromptArgument> Arguments { get; init; } = Array.Empty<McpPromptArgument>();
}

public sealed record McpPromptArgument(string Name, string Description, bool Required);

/// <summary>Result of tools/call, flattened to text for the agent loop.</summary>
public sealed record McpCallResult
{
    public required bool Success { get; init; }
    public string Content { get; init; } = string.Empty;
    public string? Error { get; init; }
}

/// <summary>Result of prompts/get, flattened to text.</summary>
public sealed record McpPromptResult
{
    public string? Description { get; init; }
    public required string Text { get; init; }
}

/// <summary>
/// Provider-neutral MCP operations the platform uses. The worker receives a
/// gateway-backed implementation; the gateway itself uses the HTTP client.
/// Note there is deliberately NO "resources" or arbitrary-request surface in
/// the first milestone — tools and prompts only, each individually allowlisted.
/// </summary>
public interface IMcpClient
{
    Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(string serverKey, CancellationToken ct = default);

    Task<McpCallResult> CallToolAsync(string serverKey, string tool, string argumentsJson, CancellationToken ct = default);

    Task<IReadOnlyList<McpPromptDescriptor>> ListPromptsAsync(string serverKey, CancellationToken ct = default);

    Task<McpPromptResult> GetPromptAsync(string serverKey, string promptName, string argumentsJson, CancellationToken ct = default);
}
