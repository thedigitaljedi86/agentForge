namespace DevAgent.Forge.Tools;

using DevAgent.Contracts.Validation;

/// <summary>
/// Allowlist of tool names the coding agent may use.
///
/// SECURITY: This is the explicit, closed set of capabilities. It deliberately
/// contains no shell, bash, powershell, curl, wget, ssh, docker, kubectl, az,
/// aws or generic "run command" tool. Even though tool calls are strongly
/// typed (so the LLM cannot fabricate a new tool), this allowlist is a second,
/// independent gate and lets an operator disable individual tools by config.
/// </summary>
public sealed class ToolPolicy
{
    public static readonly IReadOnlySet<string> DefaultAllowedTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "list_files",
        "read_file",
        "apply_patch",
        "replace_file",
        "run_dotnet_build",
        "run_dotnet_test",
        "git_status",
    };

    // Names that must NEVER be allowed, checked explicitly as defence in depth.
    private static readonly IReadOnlySet<string> ForbiddenTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "bash", "sh", "shell", "powershell", "pwsh", "cmd", "exec",
        "curl", "wget", "ssh", "scp", "docker", "kubectl", "az", "aws", "gcloud",
        "run_command", "execute", "eval",
    };

    private readonly IReadOnlySet<string> _allowed;

    public ToolPolicy() : this(DefaultAllowedTools) { }

    public ToolPolicy(IReadOnlySet<string> allowed)
    {
        _allowed = allowed;
    }

    public IReadOnlyCollection<string> AllowedTools => (IReadOnlyCollection<string>)_allowed;

    /// <summary>
    /// True when the name is on the hard blocklist (bash/exec/curl/...). Used
    /// as defence in depth for MCP tool names too — even an admin-allowlisted
    /// MCP tool may not carry a shell-like name.
    /// </summary>
    public static bool IsForbiddenName(string toolName) =>
        !string.IsNullOrWhiteSpace(toolName) && ForbiddenTools.Contains(toolName);

    public bool IsAllowed(string toolName) =>
        !string.IsNullOrWhiteSpace(toolName)
        && !ForbiddenTools.Contains(toolName)
        && _allowed.Contains(toolName);

    public ValidationResult Validate(string toolName) =>
        IsAllowed(toolName)
            ? ValidationResult.Success
            : ValidationResult.Fail($"Tool '{toolName}' is not on the agent tool allowlist.");
}
