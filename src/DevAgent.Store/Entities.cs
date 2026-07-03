namespace DevAgent.Store;

using System.ComponentModel.DataAnnotations;

/// <summary>Allowlisted repository (the ONLY source of clone URLs).</summary>
public class RepositoryEntity
{
    [Key] public string Key { get; set; } = string.Empty;
    public string CloneUrl { get; set; } = string.Empty;
    public string BaseBranch { get; set; } = "main";
}

/// <summary>Allowlisted NuGet package id.</summary>
public class PackageEntity
{
    [Key] public string PackageId { get; set; } = string.Empty;
}

/// <summary>Allowlisted container image.</summary>
public class ContainerImageEntity
{
    [Key] public string Image { get; set; } = string.Empty;
}

/// <summary>Job type → the allowlisted image it runs in.</summary>
public class JobTypeImageEntity
{
    [Key] public string JobType { get; set; } = string.Empty;
    public string Image { get; set; } = string.Empty;
}

/// <summary>Allowlisted target framework for DotNetUpgrade jobs.</summary>
public class TargetFrameworkEntity
{
    [Key] public string Framework { get; set; } = string.Empty;
}

/// <summary>Declarative "repository X uses package Y" fact (feeds the usage scanner).</summary>
public class PackageUsageEntity
{
    public string RepositoryKey { get; set; } = string.Empty;
    public string PackageId { get; set; } = string.Empty;
    public string? CurrentVersion { get; set; }
}

/// <summary>
/// Registered MCP server. AuthTokenEnvVar is a secret REFERENCE (env var name
/// on the gateway host) — the secret value is never stored here.
/// </summary>
public class McpServerEntity
{
    [Key] public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public string? AuthHeaderName { get; set; }
    public string? AuthTokenEnvVar { get; set; }

    /// <summary>JSON array of allowlisted tool names.</summary>
    public string AllowedToolsJson { get; set; } = "[]";

    /// <summary>JSON array of allowlisted prompt names.</summary>
    public string AllowedPromptsJson { get; set; } = "[]";

    public bool Enabled { get; set; } = true;
}

/// <summary>
/// A skill: a named instruction package for the coding agent. Instructions are
/// either inline markdown, or fetched from a registered MCP server's prompt
/// (McpServerKey + McpPromptName) at job time. A skill never grants tools —
/// RequiredTools are CHECKED against the agent's grants, not added to them.
/// </summary>
public class SkillEntity
{
    [Key] public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>Inline markdown instructions (used when no MCP prompt is set).</summary>
    public string Instructions { get; set; } = string.Empty;

    /// <summary>Optional: source the instructions from this MCP server's prompt.</summary>
    public string? McpServerKey { get; set; }
    public string? McpPromptName { get; set; }
    public string McpPromptArgsJson { get; set; } = "{}";

    /// <summary>JSON array of tool wire-names this skill needs (builtin or mcp__*).</summary>
    public string RequiredToolsJson { get; set; } = "[]";

    public bool Enabled { get; set; } = true;
}

/// <summary>Inbound webhook configuration (enable/disable + shared secret).</summary>
public class WebhookEntity
{
    [Key] public string Key { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Optional shared secret the caller must present in X-DevAgent-Secret.
    /// Empty = no secret required (e.g. trusted internal network).
    /// </summary>
    public string? SharedSecret { get; set; }
}

/// <summary>Per-agent settings: watch lists, model pin, MCP grants, skills.</summary>
public class AgentSettingEntity
{
    [Key] public string AgentName { get; set; } = string.Empty;

    /// <summary>JSON array of repository keys the agent watches.</summary>
    public string RepositoryKeysJson { get; set; } = "[]";

    /// <summary>JSON array of package ids (DependencyPilot).</summary>
    public string WatchedPackagesJson { get; set; } = "[]";

    /// <summary>Target framework (DotNetUpgrader).</summary>
    public string? TargetFramework { get; set; }

    public bool IncludePrerelease { get; set; }

    /// <summary>LLM pin for the agent's repair step ("Claude"/"OpenAi"/"Gemini"); null = disabled.</summary>
    public string? LlmProvider { get; set; }
    public string? LlmModel { get; set; }

    /// <summary>JSON array of { serverKey, tools: [], prompts: [] } grants.</summary>
    public string McpGrantsJson { get; set; } = "[]";

    /// <summary>JSON array of skill keys the agent applies to repair tasks.</summary>
    public string SkillKeysJson { get; set; } = "[]";
}

/// <summary>The single local admin login. Only a PBKDF2 hash is stored.</summary>
public class AdminUserEntity
{
    [Key] public string Username { get; set; } = "admin";
    public string PasswordHashBase64 { get; set; } = string.Empty;
    public string SaltBase64 { get; set; } = string.Empty;
    public int Iterations { get; set; } = 210_000;
}

/// <summary>Append-only log of admin configuration changes.</summary>
public class ConfigChangeEntity
{
    [Key] public long Id { get; set; }
    public DateTimeOffset AtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string User { get; set; } = string.Empty;
    public string Section { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
}
