namespace DevAgent.Bridge.NuGet;

using System.Net;
using System.Net.Http.Json;

/// <summary>Configuration for the NuGet feed the provider queries.</summary>
public sealed class NuGetFeedOptions
{
    public const string SectionName = "NuGetFeed";

    /// <summary>
    /// Base URL of the V3 feed host. Defaults to nuget.org; point this at an
    /// internal feed (Artifactory/Nexus/Azure Artifacts) in production.
    /// </summary>
    public string BaseUrl { get; set; } = "https://api.nuget.org";
}

/// <summary>
/// Real <see cref="INuGetPackageProvider"/> backed by the NuGet V3
/// "flat container" API: GET {base}/v3-flatcontainer/{lowercase-id}/index.json
/// returns { "versions": [ ... ] }.
///
/// This component only READS public package metadata — it never authenticates
/// with credentials and never publishes anything. Which packages may actually
/// be updated is still governed by the Guard allowlists downstream.
/// </summary>
public sealed class HttpNuGetPackageProvider : INuGetPackageProvider
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public HttpNuGetPackageProvider(HttpClient http, NuGetFeedOptions? options = null)
    {
        _http = http;
        _baseUrl = (options?.BaseUrl ?? "https://api.nuget.org").TrimEnd('/');
    }

    public async Task<IReadOnlyList<NuGetPackageVersion>> GetVersionsAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return Array.Empty<NuGetPackageVersion>();
        }

        var url = $"{_baseUrl}/v3-flatcontainer/{packageId.ToLowerInvariant()}/index.json";

        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return Array.Empty<NuGetPackageVersion>();
        }

        response.EnsureSuccessStatusCode();

        var payload = await response.Content
            .ReadFromJsonAsync<FlatContainerIndex>(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return (payload?.Versions ?? new List<string>())
            .Select(v => new NuGetPackageVersion
            {
                PackageId = packageId,
                Version = v,
                IsPrerelease = v.Contains('-'),
            })
            .ToList();
    }

    public async Task<NuGetPackageVersion?> GetLatestVersionAsync(
        string packageId,
        bool includePrerelease = false,
        CancellationToken cancellationToken = default)
    {
        var versions = await GetVersionsAsync(packageId, cancellationToken).ConfigureAwait(false);

        // The flat-container index is sorted ascending on nuget.org, but we
        // sort defensively so custom feeds with unordered indexes still work.
        return versions
            .Where(v => includePrerelease || !v.IsPrerelease)
            .OrderBy(v => v.Version, NuGetVersionComparer.Instance)
            .LastOrDefault();
    }

    private sealed record FlatContainerIndex(List<string> Versions);
}

/// <summary>
/// Pragmatic SemVer-ish comparer for NuGet version strings: numeric segment
/// comparison for the release part, release &gt; prerelease of the same
/// release, prerelease tags compared ordinally.
/// </summary>
public sealed class NuGetVersionComparer : IComparer<string>
{
    public static NuGetVersionComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null)
        {
            return string.CompareOrdinal(x, y);
        }

        var (xRelease, xPre) = Split(x);
        var (yRelease, yPre) = Split(y);

        var releaseCompare = CompareRelease(xRelease, yRelease);
        if (releaseCompare != 0)
        {
            return releaseCompare;
        }

        // Same release part: no prerelease tag ranks higher than any tag.
        if (xPre is null && yPre is null) return 0;
        if (xPre is null) return 1;
        if (yPre is null) return -1;
        return string.CompareOrdinal(xPre, yPre);
    }

    private static (string Release, string? Prerelease) Split(string version)
    {
        var dash = version.IndexOf('-');
        return dash < 0 ? (version, null) : (version[..dash], version[(dash + 1)..]);
    }

    private static int CompareRelease(string a, string b)
    {
        var aParts = a.Split('.');
        var bParts = b.Split('.');
        var length = Math.Max(aParts.Length, bParts.Length);

        for (var i = 0; i < length; i++)
        {
            var aNum = i < aParts.Length && int.TryParse(aParts[i], out var an) ? an : 0;
            var bNum = i < bParts.Length && int.TryParse(bParts[i], out var bn) ? bn : 0;
            if (aNum != bNum)
            {
                return aNum.CompareTo(bNum);
            }
        }

        return 0;
    }
}
