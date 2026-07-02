namespace Agents.DependencyPilot.Tests;

using global::Agents.DependencyPilot;
using DevAgent.Bridge.NuGet;
using DevAgent.Contracts.Jobs;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>
/// Tests for the webhook-driven entry point: a feed announces a new package
/// version. The watch lists must gate everything.
/// </summary>
public class PackagePublishedHandlingTests
{
    private sealed class MapScanner : IPackageUsageScanner
    {
        private readonly Dictionary<(string Repo, string Package), string?> _usage;

        public MapScanner(Dictionary<(string, string), string?> usage) => _usage = usage;

        public Task<PackageUsageResult> ScanAsync(string repositoryKey, string packageId, CancellationToken ct = default)
        {
            var used = _usage.TryGetValue((repositoryKey, packageId), out var current);
            return Task.FromResult(new PackageUsageResult
            {
                RepositoryKey = repositoryKey,
                PackageId = packageId,
                IsUsed = used,
                CurrentVersion = current,
            });
        }
    }

    private sealed class NoProvider : INuGetPackageProvider
    {
        public Task<NuGetPackageVersion?> GetLatestVersionAsync(string packageId, bool includePrerelease = false, CancellationToken ct = default)
            => Task.FromResult<NuGetPackageVersion?>(null);

        public Task<IReadOnlyList<NuGetPackageVersion>> GetVersionsAsync(string packageId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<NuGetPackageVersion>>(Array.Empty<NuGetPackageVersion>());
    }

    private sealed class RecordingTrigger : IDependencyUpdateTrigger
    {
        public List<NuGetUpdateJobRequest> Requests { get; } = new();

        public Task<AgentJobResult> StartDependencyUpdateAsync(NuGetUpdateJobRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(new AgentJobResult { JobId = request.JobId, Status = AgentJobStatus.Validated });
        }
    }

    private static (DependencyPilotService service, RecordingTrigger trigger) NewService(
        Dictionary<(string, string), string?> usage)
    {
        var options = new DependencyPilotOptions
        {
            RepositoryKeys = { "svc-a", "svc-b" },
            WatchedPackages = { "Serilog" },
        };
        var trigger = new RecordingTrigger();
        var service = new DependencyPilotService(
            Options.Create(options), new NoProvider(), new MapScanner(usage), trigger);
        return (service, trigger);
    }

    [Fact]
    public async Task Unwatched_package_is_rejected_and_nothing_starts()
    {
        var (service, trigger) = NewService(new());

        var results = await service.HandlePackagePublishedAsync("EvilPackage", "9.9.9");

        var result = Assert.Single(results);
        Assert.Equal(AgentJobStatus.Rejected, result.Status);
        Assert.Empty(trigger.Requests);
    }

    [Fact]
    public async Task Watched_package_starts_workflows_only_for_affected_repos()
    {
        var (service, trigger) = NewService(new()
        {
            [("svc-a", "Serilog")] = "2.0.0", // uses it, older version
            // svc-b does not use Serilog
        });

        var results = await service.HandlePackagePublishedAsync("Serilog", "3.1.1");

        Assert.Single(results);
        var request = Assert.Single(trigger.Requests);
        Assert.Equal("svc-a", request.RepositoryKey);
        Assert.Equal("3.1.1", request.TargetVersion);
    }

    [Fact]
    public async Task Repo_already_on_published_version_is_skipped()
    {
        var (service, trigger) = NewService(new()
        {
            [("svc-a", "Serilog")] = "3.1.1", // already current
        });

        var results = await service.HandlePackagePublishedAsync("Serilog", "3.1.1");

        Assert.Empty(results);
        Assert.Empty(trigger.Requests);
    }

    [Fact]
    public async Task Blank_payload_is_rejected()
    {
        var (service, trigger) = NewService(new());

        var results = await service.HandlePackagePublishedAsync("", "");

        Assert.Equal(AgentJobStatus.Rejected, Assert.Single(results).Status);
        Assert.Empty(trigger.Requests);
    }
}
