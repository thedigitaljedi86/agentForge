namespace Agents.DotNetUpgrader.Tests;

using global::Agents.DotNetUpgrader;
using DevAgent.Contracts.Jobs;
using Microsoft.Extensions.Options;
using Xunit;

public class DotNetUpgradeServiceTests
{
    private static DotNetUpgradeService NewService(DotNetUpgraderOptions options, RecordingTrigger trigger)
        => new(Options.Create(options), trigger);

    [Fact]
    public void PlanUpgrades_emits_one_candidate_per_watched_repo_at_the_target_framework()
    {
        var options = new DotNetUpgraderOptions
        {
            RepositoryKeys = { "svc-a", "svc-b" },
            TargetFramework = "net8.0",
        };

        var candidates = NewService(options, new RecordingTrigger()).PlanUpgrades();

        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, c => Assert.Equal("net8.0", c.TargetFramework));
        Assert.Contains(candidates, c => c.RepositoryKey == "svc-a");
        Assert.Contains(candidates, c => c.RepositoryKey == "svc-b");
    }

    [Fact]
    public async Task StartUpgrade_triggers_platform_for_watched_repo()
    {
        var options = new DotNetUpgraderOptions { RepositoryKeys = { "svc-a" }, TargetFramework = "net8.0" };
        var trigger = new RecordingTrigger();
        var service = NewService(options, trigger);

        var result = await service.StartUpgradeWorkflowAsync(new DotNetUpgradeCandidate("svc-a", "net8.0"));

        Assert.NotEqual(AgentJobStatus.Rejected, result.Status);
        Assert.NotNull(trigger.LastRequest);
        Assert.Equal("svc-a", trigger.LastRequest!.RepositoryKey);
        Assert.Equal("net8.0", trigger.LastRequest.TargetFramework);
        Assert.Equal(AgentJobType.DotNetUpgrade, trigger.LastRequest.JobType);
    }

    [Fact]
    public async Task StartUpgrade_rejects_repo_outside_watch_list()
    {
        var options = new DotNetUpgraderOptions { RepositoryKeys = { "svc-a" }, TargetFramework = "net8.0" };
        var trigger = new RecordingTrigger();
        var service = NewService(options, trigger);

        var result = await service.StartUpgradeWorkflowAsync(new DotNetUpgradeCandidate("svc-evil", "net8.0"));

        Assert.Equal(AgentJobStatus.Rejected, result.Status);
        Assert.Null(trigger.LastRequest); // never reached the platform
    }

    private sealed class RecordingTrigger : IDotNetUpgradeTrigger
    {
        public DotNetUpgradeJobRequest? LastRequest { get; private set; }

        public Task<AgentJobResult> StartDotNetUpgradeAsync(DotNetUpgradeJobRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new AgentJobResult { JobId = request.JobId, Status = AgentJobStatus.Validated });
        }
    }
}
