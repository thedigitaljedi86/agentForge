namespace DevAgent.Contracts.Jobs;

/// <summary>
/// Request to update a single NuGet package in a single repository.
///
/// SECURITY: <see cref="RepositoryKey"/> and <see cref="PackageId"/> are
/// allowlist keys, not raw URLs or arbitrary identifiers. The Runner resolves
/// the repository key to a trusted clone URL and validates the package id
/// against the package allowlist before any worker is started.
/// </summary>
public sealed record NuGetUpdateJobRequest : AgentJobRequest
{
    public override AgentJobType JobType => AgentJobType.NuGetUpdate;

    /// <summary>Allowlist key identifying the target repository.</summary>
    public required string RepositoryKey { get; init; }

    /// <summary>Allowlisted NuGet package id (e.g. "Serilog").</summary>
    public required string PackageId { get; init; }

    /// <summary>The version to update to (e.g. "3.1.1").</summary>
    public required string TargetVersion { get; init; }

    /// <summary>
    /// Optional: only update if the current version is lower than the target.
    /// Defaults to true so we never downgrade a dependency.
    /// </summary>
    public bool OnlyUpgrade { get; init; } = true;
}
