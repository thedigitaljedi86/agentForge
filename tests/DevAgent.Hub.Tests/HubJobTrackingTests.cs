namespace DevAgent.Hub.Tests;

using DevAgent.Audit;
using DevAgent.Contracts.Jobs;
using DevAgent.Hub.Api.Application;
using DevAgent.Hub.Api.Jobs;
using Xunit;

public class InMemoryJobTrackerTests
{
    [Fact]
    public void Records_and_snapshots_a_job()
    {
        var tracker = new InMemoryJobTracker();
        tracker.Upsert("j1", "manual", "NuGetUpdate", "svc-a: Serilog@3.1.1", AgentJobStatus.Pending, "Accepted.");

        var job = Assert.Single(tracker.Snapshot());
        Assert.Equal("j1", job.JobId);
        Assert.Equal(AgentJobStatus.Pending, job.Status);
    }

    [Fact]
    public void Status_update_preserves_received_time()
    {
        var tracker = new InMemoryJobTracker();
        tracker.Upsert("j1", "manual", "NuGetUpdate", "t", AgentJobStatus.Pending, null);
        var received = tracker.Snapshot()[0].ReceivedAtUtc;

        tracker.Upsert("j1", "manual", "NuGetUpdate", "t", AgentJobStatus.Succeeded, "done");

        var updated = Assert.Single(tracker.Snapshot());
        Assert.Equal(AgentJobStatus.Succeeded, updated.Status);
        Assert.Equal(received, updated.ReceivedAtUtc);
    }
}

public class HubDependencyUpdateTriggerTests
{
    private sealed class FakeRunnerClient : IRunnerClient
    {
        public NuGetUpdateJobRequest? LastRequest { get; private set; }

        public Task<AgentJobResult> StartNuGetUpdateAsync(NuGetUpdateJobRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new AgentJobResult
            {
                JobId = request.JobId,
                Status = AgentJobStatus.Succeeded,
                PullRequestUrl = "https://git/pr/42",
            });
        }

        public Task<AgentJobResult> StartDotNetUpgradeAsync(DotNetUpgradeJobRequest request, CancellationToken ct = default)
            => Task.FromResult(new AgentJobResult { JobId = request.JobId, Status = AgentJobStatus.Succeeded });

        public Task<AgentJobResult> StartPipelineFixAsync(PipelineFixJobRequest request, CancellationToken ct = default)
            => Task.FromResult(new AgentJobResult { JobId = request.JobId, Status = AgentJobStatus.Succeeded });

        public Task<AgentJobResult> StartDocUpdateAsync(DocUpdateJobRequest request, CancellationToken ct = default)
            => Task.FromResult(new AgentJobResult { JobId = request.JobId, Status = AgentJobStatus.Succeeded });

        public Task<AgentJobResult> StartCodeReviewAsync(CodeReviewJobRequest request, CancellationToken ct = default)
            => Task.FromResult(new AgentJobResult { JobId = request.JobId, Status = AgentJobStatus.Succeeded });
    }

    [Fact]
    public async Task Trigger_routes_agent_requests_through_the_service_preserving_job_id()
    {
        var tracker = new InMemoryJobTracker();
        var runner = new FakeRunnerClient();
        var service = new HubJobApplicationService(runner, new ConsoleAuditLog(), tracker);
        var trigger = new HubDependencyUpdateTrigger(service);

        var request = new NuGetUpdateJobRequest
        {
            RepositoryKey = "svc-a",
            PackageId = "Serilog",
            TargetVersion = "3.1.1",
            RequestedBy = "DependencyPilot",
        };

        var result = await trigger.StartDependencyUpdateAsync(request);

        Assert.Equal(request.JobId, result.JobId);                  // same job id end to end
        Assert.Equal(request.JobId, runner.LastRequest!.JobId);     // forwarded to the Runner
        Assert.Contains(tracker.Snapshot(), j => j.JobId == request.JobId
            && j.Status == AgentJobStatus.Succeeded
            && j.Agent == "DependencyPilot");                       // tracked for the dashboard
    }
}
