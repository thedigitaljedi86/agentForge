namespace Agents.DependencyPilot.Tests;

using global::Agents.DependencyPilot;
using DevAgent.Bridge.NuGet;
using DevAgent.Contracts.Jobs;
using Microsoft.Extensions.Options;
using Xunit;

public class DependencyPilotServiceTests
{
    private static DependencyPilotService NewService(
        DependencyPilotOptions options,
        INuGetPackageProvider provider,
        IPackageUsageScanner scanner,
        RecordingTrigger trigger)
        => new(Options.Create(options), provider, scanner, trigger);

    [Fact]
    public async Task CheckForPackageUpdates_emits_candidate_for_affected_repo()
    {
        var options = new DependencyPilotOptions
        {
            RepositoryKeys = { "svc-a" },
            WatchedPackages = { "Serilog" },
        };
        var provider = new FakeProvider("Serilog", "3.1.1");
        var scanner = new FakeScanner(isUsed: true, currentVersion: "2.0.0");

        var service = NewService(options, provider, scanner, new RecordingTrigger());
        var candidates = await service.CheckForPackageUpdatesAsync();

        var candidate = Assert.Single(candidates);
        Assert.Equal("svc-a", candidate.RepositoryKey);
        Assert.Equal("Serilog", candidate.PackageId);
        Assert.Equal("3.1.1", candidate.TargetVersion);
    }

    [Fact]
    public async Task CheckForPackageUpdates_skips_repo_already_on_latest()
    {
        var options = new DependencyPilotOptions { RepositoryKeys = { "svc-a" }, WatchedPackages = { "Serilog" } };
        var provider = new FakeProvider("Serilog", "3.1.1");
        var scanner = new FakeScanner(isUsed: true, currentVersion: "3.1.1");

        var service = NewService(options, provider, scanner, new RecordingTrigger());
        Assert.Empty(await service.CheckForPackageUpdatesAsync());
    }

    [Fact]
    public async Task StartWorkflow_triggers_platform_for_watched_candidate()
    {
        var options = new DependencyPilotOptions { RepositoryKeys = { "svc-a" }, WatchedPackages = { "Serilog" } };
        var trigger = new RecordingTrigger();
        var service = NewService(options, new FakeProvider("Serilog", "3.1.1"), new FakeScanner(true, "2.0.0"), trigger);

        var result = await service.StartDependencyUpdateWorkflowAsync(
            new DependencyUpdateCandidate("svc-a", "Serilog", "3.1.1"));

        Assert.NotEqual(AgentJobStatus.Rejected, result.Status);
        Assert.NotNull(trigger.LastRequest);
        Assert.Equal("svc-a", trigger.LastRequest!.RepositoryKey);
    }

    [Fact]
    public async Task StartWorkflow_rejects_repo_outside_watch_list()
    {
        var options = new DependencyPilotOptions { RepositoryKeys = { "svc-a" }, WatchedPackages = { "Serilog" } };
        var trigger = new RecordingTrigger();
        var service = NewService(options, new FakeProvider("Serilog", "3.1.1"), new FakeScanner(true, "2.0.0"), trigger);

        var result = await service.StartDependencyUpdateWorkflowAsync(
            new DependencyUpdateCandidate("svc-evil", "Serilog", "3.1.1"));

        Assert.Equal(AgentJobStatus.Rejected, result.Status);
        Assert.Null(trigger.LastRequest); // never reached the platform
    }

    [Fact]
    public async Task StartWorkflow_rejects_package_outside_watch_list()
    {
        var options = new DependencyPilotOptions { RepositoryKeys = { "svc-a" }, WatchedPackages = { "Serilog" } };
        var trigger = new RecordingTrigger();
        var service = NewService(options, new FakeProvider("Serilog", "3.1.1"), new FakeScanner(true, "2.0.0"), trigger);

        var result = await service.StartDependencyUpdateWorkflowAsync(
            new DependencyUpdateCandidate("svc-a", "EvilPackage", "9.9.9"));

        Assert.Equal(AgentJobStatus.Rejected, result.Status);
        Assert.Null(trigger.LastRequest);
    }

    // --- fakes ---

    private sealed class FakeProvider : INuGetPackageProvider
    {
        private readonly NuGetPackageVersion _latest;
        public FakeProvider(string id, string version) =>
            _latest = new NuGetPackageVersion { PackageId = id, Version = version };

        public Task<NuGetPackageVersion?> GetLatestVersionAsync(string packageId, bool includePrerelease = false, CancellationToken ct = default)
            => Task.FromResult<NuGetPackageVersion?>(_latest);

        public Task<IReadOnlyList<NuGetPackageVersion>> GetVersionsAsync(string packageId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<NuGetPackageVersion>>(new[] { _latest });
    }

    private sealed class FakeScanner : IPackageUsageScanner
    {
        private readonly bool _isUsed;
        private readonly string? _current;
        public FakeScanner(bool isUsed, string? currentVersion) { _isUsed = isUsed; _current = currentVersion; }

        public Task<PackageUsageResult> ScanAsync(string repositoryKey, string packageId, CancellationToken ct = default)
            => Task.FromResult(new PackageUsageResult
            {
                RepositoryKey = repositoryKey,
                PackageId = packageId,
                IsUsed = _isUsed,
                CurrentVersion = _current,
            });
    }

    private sealed class RecordingTrigger : IDependencyUpdateTrigger
    {
        public NuGetUpdateJobRequest? LastRequest { get; private set; }

        public Task<AgentJobResult> StartDependencyUpdateAsync(NuGetUpdateJobRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new AgentJobResult { JobId = request.JobId, Status = AgentJobStatus.Validated });
        }
    }
}
