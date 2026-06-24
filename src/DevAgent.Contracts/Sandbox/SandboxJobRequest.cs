namespace DevAgent.Contracts.Sandbox;

using DevAgent.Contracts.Jobs;

/// <summary>
/// A fully-resolved, validated job ready to run inside a sandbox worker
/// container. This is produced by the Runner AFTER allowlist validation —
/// the values here are trusted because they came from policy, not the caller.
///
/// SECURITY: Even though <see cref="CloneUrl"/> and <see cref="ContainerImage"/>
/// are concrete values here, they originate from the Runner's allowlist
/// resolution, never directly from an API caller. The worker receives these
/// as environment variables; it does not receive secrets, Docker arguments,
/// or host paths.
/// </summary>
public sealed record SandboxJobRequest
{
    public required string JobId { get; init; }

    public required AgentJobType JobType { get; init; }

    /// <summary>Resolved (trusted) clone URL for the repository.</summary>
    public required string CloneUrl { get; init; }

    /// <summary>Default branch to base the work branch on (e.g. "main").</summary>
    public required string BaseBranch { get; init; }

    /// <summary>Allowlisted container image the worker must run in.</summary>
    public required string ContainerImage { get; init; }

    /// <summary>NuGet package id to update (for NuGetUpdate jobs).</summary>
    public string? PackageId { get; init; }

    /// <summary>Target package version (for NuGetUpdate jobs).</summary>
    public string? TargetVersion { get; init; }

    /// <summary>Target framework to upgrade all projects to (for DotNetUpgrade jobs).</summary>
    public string? TargetFramework { get; init; }

    /// <summary>Whether the update should refuse to downgrade.</summary>
    public bool OnlyUpgrade { get; init; } = true;
}
