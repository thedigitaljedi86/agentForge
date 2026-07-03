namespace DevAgent.Bridge.NuGet.Tests;

using System.Net;
using System.Text;
using DevAgent.Bridge.NuGet;
using Xunit;

public class HttpNuGetPackageProviderTests
{
    /// <summary>Handler returning a canned response and recording the request URL.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _json;

        public Uri? LastRequestUri { get; private set; }

        public FakeHandler(HttpStatusCode status, string json)
        {
            _status = status;
            _json = json;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (HttpNuGetPackageProvider provider, FakeHandler handler) NewProvider(
        string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new FakeHandler(status, json);
        var provider = new HttpNuGetPackageProvider(new HttpClient(handler));
        return (provider, handler);
    }

    [Fact]
    public async Task Queries_flatcontainer_with_lowercased_package_id()
    {
        var (provider, handler) = NewProvider("""{"versions":["1.0.0"]}""");

        await provider.GetVersionsAsync("Serilog");

        Assert.Equal(
            "https://api.nuget.org/v3-flatcontainer/serilog/index.json",
            handler.LastRequestUri!.ToString());
    }

    [Fact]
    public async Task Latest_stable_version_skips_prereleases()
    {
        var (provider, _) = NewProvider("""{"versions":["1.0.0","2.0.0","3.0.0-beta.1"]}""");

        var latest = await provider.GetLatestVersionAsync("Serilog");

        Assert.Equal("2.0.0", latest!.Version);
        Assert.False(latest.IsPrerelease);
    }

    [Fact]
    public async Task Latest_with_prerelease_included_returns_the_prerelease()
    {
        var (provider, _) = NewProvider("""{"versions":["1.0.0","2.0.0","3.0.0-beta.1"]}""");

        var latest = await provider.GetLatestVersionAsync("Serilog", includePrerelease: true);

        Assert.Equal("3.0.0-beta.1", latest!.Version);
        Assert.True(latest.IsPrerelease);
    }

    [Fact]
    public async Task Handles_unordered_feed_indexes()
    {
        var (provider, _) = NewProvider("""{"versions":["2.0.0","1.2.10","1.2.9","10.0.0"]}""");

        var latest = await provider.GetLatestVersionAsync("Serilog");

        Assert.Equal("10.0.0", latest!.Version); // numeric, not ordinal, comparison
    }

    [Fact]
    public async Task Unknown_package_returns_empty_and_null_latest()
    {
        var (provider, _) = NewProvider("""{"errors":["not found"]}""", HttpStatusCode.NotFound);

        Assert.Empty(await provider.GetVersionsAsync("NoSuchPackage"));
        Assert.Null(await provider.GetLatestVersionAsync("NoSuchPackage"));
    }

    [Theory]
    [InlineData("1.2.9", "1.2.10", -1)]
    [InlineData("2.0.0", "2.0.0", 0)]
    [InlineData("2.0.0-beta", "2.0.0", -1)] // release beats its prerelease
    [InlineData("2.0.0-alpha", "2.0.0-beta", -1)]
    [InlineData("1.0", "1.0.0", 0)] // missing segments are zero
    public void Version_comparer_orders_correctly(string a, string b, int expectedSign)
    {
        Assert.Equal(expectedSign, Math.Sign(NuGetVersionComparer.Instance.Compare(a, b)));
    }
}

public class ConfiguredPackageUsageScannerTests
{
    private static ConfiguredPackageUsageScanner NewScanner() => new(new PackageUsageMapOptions
    {
        Repositories = new Dictionary<string, List<ConfiguredPackageUsage>>
        {
            ["svc-a"] = new()
            {
                new ConfiguredPackageUsage { PackageId = "Serilog", CurrentVersion = "2.0.0" },
            },
        },
    });

    [Fact]
    public async Task Reports_configured_usage_with_current_version()
    {
        var result = await NewScanner().ScanAsync("svc-a", "serilog"); // case-insensitive

        Assert.True(result.IsUsed);
        Assert.Equal("2.0.0", result.CurrentVersion);
    }

    [Fact]
    public async Task Reports_not_used_for_unknown_repo_or_package()
    {
        var scanner = NewScanner();

        Assert.False((await scanner.ScanAsync("svc-a", "Polly")).IsUsed);
        Assert.False((await scanner.ScanAsync("svc-unknown", "Serilog")).IsUsed);
    }
}
