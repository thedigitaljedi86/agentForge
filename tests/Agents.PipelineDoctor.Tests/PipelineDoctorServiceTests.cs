namespace Agents.PipelineDoctor.Tests;

using Agents.PipelineDoctor;
using DevAgent.Bridge.Ci;
using DevAgent.Contracts.Jobs;
using Microsoft.Extensions.Options;
using Xunit;

public class PipelineDoctorServiceTests
{
    private static readonly CiConnection Connection = new()
    {
        RepositoryKey = "svc-a",
        Provider = CiProviderKind.GitHubActions,
        BaseUrl = "https://api.github.example",
        ProjectPath = "org/svc-a",
        TokenEnvVar = "CI_TOKEN",
    };

    private static PipelineDoctorService NewService(
        FakeCiProvider ci,
        RecordingTrigger trigger,
        InMemoryProcessedStore processed,
        params string[] watched)
    {
        var options = Options.Create(new PipelineDoctorOptions { RepositoryKeys = watched.ToList() });
        var connections = new FakeConnectionSource(Connection);
        return new PipelineDoctorService(options, connections, processed, new FakeProviderFactory(ci), trigger);
    }

    [Fact]
    public async Task Unwatched_repository_is_refused_without_touching_ci()
    {
        var ci = new FakeCiProvider();
        var trigger = new RecordingTrigger();
        var service = NewService(ci, trigger, new InMemoryProcessedStore(), "other-repo");

        var findings = await service.SweepRepositoryAsync("svc-a");

        Assert.Single(findings);
        Assert.Equal(AgentJobStatus.Rejected, findings[0].Result.Status);
        Assert.Empty(trigger.Requests);
        Assert.Equal(0, ci.ListCalls);
    }

    [Fact]
    public async Task New_failures_become_pipeline_fix_proposals_with_the_log_as_context()
    {
        var ci = new FakeCiProvider();
        ci.FailedRuns.Add(new CiPipelineRun { RunId = "run-1", Branch = "feature/x" });
        ci.Logs["run-1"] = "error CS1002: ; expected";

        var trigger = new RecordingTrigger();
        var service = NewService(ci, trigger, new InMemoryProcessedStore(), "svc-a");

        var findings = await service.SweepRepositoryAsync("svc-a");

        var request = Assert.Single(trigger.Requests);
        Assert.Equal("svc-a", request.RepositoryKey);
        Assert.Equal("feature/x", request.Branch);
        Assert.Contains("error CS1002", request.FailureContext);
        Assert.Equal("PipelineDoctor", request.RequestedBy);
        Assert.Single(findings);
    }

    [Fact]
    public async Task Already_processed_runs_are_skipped()
    {
        var ci = new FakeCiProvider();
        ci.FailedRuns.Add(new CiPipelineRun { RunId = "run-1", Branch = "main" });
        ci.Logs["run-1"] = "boom";

        var processed = new InMemoryProcessedStore();
        var trigger = new RecordingTrigger();
        var service = NewService(ci, trigger, processed, "svc-a");

        // First sweep handles the run; second sweep must skip it.
        await service.SweepRepositoryAsync("svc-a");
        var second = await service.SweepRepositoryAsync("svc-a");

        Assert.Single(trigger.Requests);
        Assert.Empty(second);
    }

    [Fact]
    public async Task Runs_are_marked_processed_even_when_the_proposal_is_rejected()
    {
        var ci = new FakeCiProvider();
        ci.FailedRuns.Add(new CiPipelineRun { RunId = "run-9", Branch = "main" });
        ci.Logs["run-9"] = "boom";

        var trigger = new RecordingTrigger { Reject = true };
        var processed = new InMemoryProcessedStore();
        var service = NewService(ci, trigger, processed, "svc-a");

        await service.SweepRepositoryAsync("svc-a");
        await service.SweepRepositoryAsync("svc-a");

        // No endless re-proposal loop of a rejected run.
        Assert.Single(trigger.Requests);
    }

    [Fact]
    public async Task Repository_without_a_ci_connection_is_a_quiet_no_op()
    {
        var options = Options.Create(new PipelineDoctorOptions { RepositoryKeys = new List<string> { "svc-a" } });
        var trigger = new RecordingTrigger();
        var service = new PipelineDoctorService(
            options, new FakeConnectionSource(null), new InMemoryProcessedStore(),
            new FakeProviderFactory(new FakeCiProvider()), trigger);

        var findings = await service.SweepRepositoryAsync("svc-a");

        Assert.Empty(findings);
        Assert.Empty(trigger.Requests);
    }

    // --- fakes ---

    private sealed class FakeConnectionSource : ICiConnectionSource
    {
        private readonly CiConnection? _connection;
        public FakeConnectionSource(CiConnection? connection) => _connection = connection;

        public Task<CiConnection?> GetAsync(string repositoryKey, CancellationToken ct = default) =>
            Task.FromResult(_connection);
    }

    private sealed class FakeProviderFactory : CiProviderFactory
    {
        private readonly ICiProvider _provider;
        public FakeProviderFactory(ICiProvider provider) : base(new HttpClient()) => _provider = provider;

        public override ICiProvider Create(CiProviderKind kind) => _provider;
    }

    private sealed class FakeCiProvider : ICiProvider
    {
        public List<CiPipelineRun> FailedRuns { get; } = new();
        public Dictionary<string, string> Logs { get; } = new();
        public int ListCalls { get; private set; }

        public Task<IReadOnlyList<CiPipelineRun>> ListFailedRunsAsync(CiConnection connection, int top = 10, CancellationToken ct = default)
        {
            ListCalls++;
            return Task.FromResult<IReadOnlyList<CiPipelineRun>>(FailedRuns.Take(top).ToList());
        }

        public Task<string> GetFailureLogAsync(CiConnection connection, string runId, CancellationToken ct = default) =>
            Task.FromResult(Logs.GetValueOrDefault(runId, string.Empty));
    }

    private sealed class InMemoryProcessedStore : IProcessedRunStore
    {
        private readonly HashSet<string> _seen = new();

        public Task<bool> IsProcessedAsync(string repositoryKey, string runId, CancellationToken ct = default) =>
            Task.FromResult(_seen.Contains($"{repositoryKey}:{runId}"));

        public Task MarkProcessedAsync(string repositoryKey, string runId, CancellationToken ct = default)
        {
            _seen.Add($"{repositoryKey}:{runId}");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingTrigger : IPipelineFixTrigger
    {
        public List<PipelineFixJobRequest> Requests { get; } = new();
        public bool Reject { get; init; }

        public Task<AgentJobResult> StartPipelineFixAsync(PipelineFixJobRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(Reject
                ? AgentJobResult.Rejected(request.JobId, "rejected by test")
                : new AgentJobResult { JobId = request.JobId, Status = AgentJobStatus.Succeeded });
        }
    }
}
