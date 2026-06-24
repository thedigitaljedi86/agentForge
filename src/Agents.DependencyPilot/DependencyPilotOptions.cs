namespace Agents.DependencyPilot;

using DevAgent.Bridge.Llm;

/// <summary>
/// Configuration for the DependencyPilot agent. The set of repositories and
/// packages the agent watches is intentionally explicit — DependencyPilot only
/// ever proposes work for combinations an administrator listed here, and the
/// Runner still re-validates everything against its own allowlists.
/// </summary>
public sealed class DependencyPilotOptions
{
    public const string SectionName = "DependencyPilot";

    /// <summary>Allowlist keys of repositories DependencyPilot may target.</summary>
    public List<string> RepositoryKeys { get; set; } = new();

    /// <summary>NuGet package ids DependencyPilot watches for new versions.</summary>
    public List<string> WatchedPackages { get; set; } = new();

    /// <summary>Whether prerelease versions should trigger updates.</summary>
    public bool IncludePrerelease { get; set; } = false;

    /// <summary>
    /// The LLM provider and model THIS agent uses for the optional in-sandbox
    /// build-repair step (DevAgent.Forge). Each agent can pin its own model;
    /// when left at defaults it uses Claude (<c>claude-opus-4-8</c>). Bound from
    /// the "DependencyPilot:Llm" configuration section.
    /// </summary>
    public LlmClientOptions Llm { get; set; } = new();
}
