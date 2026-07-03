namespace Agents.DependencyPilot;

using DevAgent.Bridge.NuGet;
using DevAgent.Contracts.Jobs;
using Microsoft.Extensions.Options;

/// <summary>One detected update opportunity (package + repo + target version).</summary>
public sealed record DependencyUpdateCandidate(string RepositoryKey, string PackageId, string TargetVersion);

/// <summary>
/// The DependencyPilot agent. Responsibilities:
///   * Detect new versions of watched packages (via Bridge.NuGet).
///   * Determine which watched repositories use them (via Bridge.NuGet).
///   * Start a platform workflow per affected repo (via the trigger seam).
///
/// SECURITY: DependencyPilot never clones, never runs commands and never opens
/// PRs directly. It only PROPOSES work by key; the Runner validates and a
/// sandbox worker performs it. The result is always a reviewable pull request.
/// </summary>
public sealed class DependencyPilotService
{
    private readonly DependencyPilotOptions _options;
    private readonly INuGetPackageProvider _packageProvider;
    private readonly IPackageUsageScanner _usageScanner;
    private readonly IDependencyUpdateTrigger _trigger;

    public DependencyPilotService(
        IOptions<DependencyPilotOptions> options,
        INuGetPackageProvider packageProvider,
        IPackageUsageScanner usageScanner,
        IDependencyUpdateTrigger trigger)
    {
        _options = options.Value;
        _packageProvider = packageProvider;
        _usageScanner = usageScanner;
        _trigger = trigger;
    }

    /// <summary>
    /// Placeholder detection pass. For each watched package, find the latest
    /// version and the watched repositories that use it, and emit a candidate
    /// per affected repository. First milestone keeps this deterministic.
    /// </summary>
    public async Task<IReadOnlyList<DependencyUpdateCandidate>> CheckForPackageUpdatesAsync(
        CancellationToken cancellationToken = default)
    {
        var candidates = new List<DependencyUpdateCandidate>();

        foreach (var packageId in _options.WatchedPackages)
        {
            var latest = await _packageProvider.GetLatestVersionAsync(
                packageId, _options.IncludePrerelease, cancellationToken);
            if (latest is null)
            {
                continue;
            }

            foreach (var repositoryKey in _options.RepositoryKeys)
            {
                var usage = await _usageScanner.ScanAsync(repositoryKey, packageId, cancellationToken);
                if (!usage.IsUsed)
                {
                    continue;
                }

                // Only propose if the repo isn't already on the latest version.
                if (usage.CurrentVersion is not null &&
                    string.Equals(usage.CurrentVersion, latest.Version, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                candidates.Add(new DependencyUpdateCandidate(repositoryKey, packageId, latest.Version));
            }
        }

        return candidates;
    }

    /// <summary>
    /// Event-driven entry point: a feed reported that a new version of a
    /// package was published. Validated against the watch lists first — an
    /// unwatched package is rejected outright, so a webhook cannot make the
    /// platform touch anything an administrator didn't opt into.
    /// </summary>
    public async Task<IReadOnlyList<AgentJobResult>> HandlePackagePublishedAsync(
        string packageId,
        string version,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(version))
        {
            return new[] { AgentJobResult.Rejected(Guid.NewGuid().ToString("N"), "Package id and version are required.") };
        }

        if (!_options.WatchedPackages.Contains(packageId, StringComparer.OrdinalIgnoreCase))
        {
            return new[]
            {
                AgentJobResult.Rejected(
                    Guid.NewGuid().ToString("N"),
                    $"Package '{packageId}' is not watched by DependencyPilot."),
            };
        }

        var results = new List<AgentJobResult>();
        foreach (var repositoryKey in _options.RepositoryKeys)
        {
            var usage = await _usageScanner.ScanAsync(repositoryKey, packageId, cancellationToken);
            if (!usage.IsUsed)
            {
                continue;
            }

            if (usage.CurrentVersion is not null &&
                string.Equals(usage.CurrentVersion, version, StringComparison.OrdinalIgnoreCase))
            {
                continue; // already on the published version
            }

            results.Add(await StartDependencyUpdateWorkflowAsync(
                new DependencyUpdateCandidate(repositoryKey, packageId, version), cancellationToken));
        }

        return results;
    }

    /// <summary>
    /// Starts a platform workflow for a single update candidate. The candidate's
    /// repository key and package id must already be on the agent's watch lists;
    /// the Runner performs the authoritative allowlist validation afterwards.
    /// </summary>
    public async Task<AgentJobResult> StartDependencyUpdateWorkflowAsync(
        DependencyUpdateCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        // Defensive check: don't even propose work outside our own watch lists.
        if (!_options.RepositoryKeys.Contains(candidate.RepositoryKey, StringComparer.OrdinalIgnoreCase))
        {
            return AgentJobResult.Rejected(
                Guid.NewGuid().ToString("N"),
                $"Repository '{candidate.RepositoryKey}' is not watched by DependencyPilot.");
        }

        if (!_options.WatchedPackages.Contains(candidate.PackageId, StringComparer.OrdinalIgnoreCase))
        {
            return AgentJobResult.Rejected(
                Guid.NewGuid().ToString("N"),
                $"Package '{candidate.PackageId}' is not watched by DependencyPilot.");
        }

        var request = new NuGetUpdateJobRequest
        {
            RepositoryKey = candidate.RepositoryKey,
            PackageId = candidate.PackageId,
            TargetVersion = candidate.TargetVersion,
            RequestedBy = "DependencyPilot",
        };

        return await _trigger.StartDependencyUpdateAsync(request, cancellationToken);
    }
}
