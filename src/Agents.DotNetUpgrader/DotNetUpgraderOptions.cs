namespace Agents.DotNetUpgrader;

using DevAgent.Bridge.Llm;

/// <summary>
/// Configuration for the DotNetUpgrader agent. The repositories it may upgrade
/// are an explicit allowlist of keys, and the target framework every project
/// should move to is operator config. The Runner re-validates every key.
/// </summary>
public sealed class DotNetUpgraderOptions
{
    public const string SectionName = "DotNetUpgrader";

    /// <summary>Allowlist keys of repositories DotNetUpgrader may upgrade.</summary>
    public List<string> RepositoryKeys { get; set; } = new();

    /// <summary>The target framework every project should be moved to (e.g. "net10.0").</summary>
    public string TargetFramework { get; set; } = "net10.0";

    /// <summary>Only rewrite frameworks older than the target (never downgrade).</summary>
    public bool OnlyUpgrade { get; set; } = true;

    /// <summary>
    /// The LLM provider and model THIS agent asks the sandbox worker to use for
    /// the optional build-repair step (framework bumps often break the build).
    /// Each agent can pin its own model; defaults to Claude.
    /// </summary>
    public LlmClientOptions Llm { get; set; } = new();
}
