namespace DevAgent.Contracts.Jobs;

/// <summary>
/// Request to upgrade the .NET target framework of every project in a single
/// repository (e.g. move all projects to <c>net8.0</c>).
///
/// SECURITY: <see cref="RepositoryKey"/> is an allowlist key, not a raw URL. The
/// Runner resolves it to a trusted clone URL before any worker is started, and
/// the result is always a review-required pull request — never an auto-merge.
/// </summary>
public sealed record DotNetUpgradeJobRequest : AgentJobRequest
{
    public override AgentJobType JobType => AgentJobType.DotNetUpgrade;

    /// <summary>Allowlist key identifying the target repository.</summary>
    public required string RepositoryKey { get; init; }

    /// <summary>The target framework to move every project to (e.g. "net8.0").</summary>
    public required string TargetFramework { get; init; }

    /// <summary>
    /// Only rewrite frameworks OLDER than the target (never downgrade). Defaults
    /// to true so a project already on a newer framework is left untouched.
    /// </summary>
    public bool OnlyUpgrade { get; init; } = true;
}
