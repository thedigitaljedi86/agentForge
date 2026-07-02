namespace DevAgent.Bridge.NuGet;

/// <summary>One configured "repository X uses package Y at version Z" fact.</summary>
public sealed class ConfiguredPackageUsage
{
    public string PackageId { get; set; } = string.Empty;

    /// <summary>Version currently referenced, if known. Optional.</summary>
    public string? CurrentVersion { get; set; }
}

/// <summary>Bindable options: repository key -> packages it uses.</summary>
public sealed class PackageUsageMapOptions
{
    public const string SectionName = "PackageUsage";

    public Dictionary<string, List<ConfiguredPackageUsage>> Repositories { get; set; } = new();
}

/// <summary>
/// Deterministic <see cref="IPackageUsageScanner"/> backed by configuration.
/// Administrators declare which repositories use which packages; the scanner
/// simply answers from that map.
///
/// Why config instead of live scanning: the Hub deliberately has no clone
/// access to repositories (only the sandbox worker clones anything), so a live
/// scan cannot happen here without weakening the security model. A stale map
/// is safe — the sandbox update is idempotent and reports NoChange when the
/// repository is already up to date. A future implementation can replace this
/// with an index produced by the sandboxed workers themselves.
/// </summary>
public sealed class ConfiguredPackageUsageScanner : IPackageUsageScanner
{
    private readonly PackageUsageMapOptions _options;

    public ConfiguredPackageUsageScanner(PackageUsageMapOptions options)
    {
        _options = options;
    }

    public Task<PackageUsageResult> ScanAsync(
        string repositoryKey,
        string packageId,
        CancellationToken cancellationToken = default)
    {
        var usage = _options.Repositories.TryGetValue(repositoryKey, out var packages)
            ? packages.FirstOrDefault(p => string.Equals(p.PackageId, packageId, StringComparison.OrdinalIgnoreCase))
            : null;

        return Task.FromResult(new PackageUsageResult
        {
            RepositoryKey = repositoryKey,
            PackageId = packageId,
            IsUsed = usage is not null,
            CurrentVersion = usage?.CurrentVersion,
        });
    }
}
