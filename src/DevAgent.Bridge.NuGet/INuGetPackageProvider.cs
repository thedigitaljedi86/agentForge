namespace DevAgent.Bridge.NuGet;

/// <summary>A single published version of a NuGet package.</summary>
public sealed record NuGetPackageVersion
{
    public required string PackageId { get; init; }
    public required string Version { get; init; }
    public bool IsPrerelease { get; init; }
    public DateTimeOffset? PublishedUtc { get; init; }
}

/// <summary>Result of scanning a repository for usage of a package.</summary>
public sealed record PackageUsageResult
{
    public required string RepositoryKey { get; init; }
    public required string PackageId { get; init; }
    public required bool IsUsed { get; init; }

    /// <summary>Currently referenced version, if discoverable.</summary>
    public string? CurrentVersion { get; init; }

    /// <summary>Project files referencing the package (workspace-relative).</summary>
    public IReadOnlyList<string> ProjectFiles { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Looks up package metadata and available versions from a NuGet feed.
/// First milestone: interface only (no concrete feed client yet).
/// </summary>
public interface INuGetPackageProvider
{
    /// <summary>Returns the latest stable version of a package, if any.</summary>
    Task<NuGetPackageVersion?> GetLatestVersionAsync(
        string packageId,
        bool includePrerelease = false,
        CancellationToken cancellationToken = default);

    /// <summary>Returns all known versions of a package.</summary>
    Task<IReadOnlyList<NuGetPackageVersion>> GetVersionsAsync(
        string packageId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Scans repositories to determine which ones reference a given package.
/// First milestone: interface only.
/// </summary>
public interface IPackageUsageScanner
{
    Task<PackageUsageResult> ScanAsync(
        string repositoryKey,
        string packageId,
        CancellationToken cancellationToken = default);
}
