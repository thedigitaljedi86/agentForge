namespace Agents.DependencyPilot;

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
}
