namespace Agents.DocScribe.Tests;

using Agents.DocScribe;
using DevAgent.Contracts.Jobs;
using Microsoft.Extensions.Options;
using Xunit;

public class DocScribeServiceTests
{
    private static (DocScribeService service, RecordingTrigger trigger) NewService(params string[] watched)
    {
        var trigger = new RecordingTrigger();
        var service = new DocScribeService(
            Options.Create(new DocScribeOptions { RepositoryKeys = watched.ToList() }),
            trigger);
        return (service, trigger);
    }

    [Fact]
    public async Task Unwatched_repository_is_refused()
    {
        var (service, trigger) = NewService("other");

        var result = await service.StartDocUpdateWorkflowAsync("svc-a");

        Assert.Equal(AgentJobStatus.Rejected, result.Status);
        Assert.Empty(trigger.Requests);
    }

    [Fact]
    public async Task Watched_repository_becomes_a_doc_update_proposal()
    {
        var (service, trigger) = NewService("svc-a");

        var result = await service.StartDocUpdateWorkflowAsync("svc-a");

        var request = Assert.Single(trigger.Requests);
        Assert.Equal("svc-a", request.RepositoryKey);
        Assert.Equal("DocScribe", request.RequestedBy);
        Assert.Equal(AgentJobType.DocUpdate, request.JobType);
        Assert.NotEqual(AgentJobStatus.Rejected, result.Status);
    }

    [Fact]
    public async Task The_scheduled_sweep_covers_every_watched_repository()
    {
        var (service, trigger) = NewService("svc-a", "svc-b", "svc-c");

        var results = await service.SweepAsync();

        Assert.Equal(3, results.Count);
        Assert.Equal(new[] { "svc-a", "svc-b", "svc-c" }, trigger.Requests.Select(r => r.RepositoryKey));
    }

    private sealed class RecordingTrigger : IDocUpdateTrigger
    {
        public List<DocUpdateJobRequest> Requests { get; } = new();

        public Task<AgentJobResult> StartDocUpdateAsync(DocUpdateJobRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new AgentJobResult { JobId = request.JobId, Status = AgentJobStatus.Succeeded });
        }
    }
}
