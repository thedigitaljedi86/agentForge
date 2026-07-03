namespace Agents.CodeReviewer.Tests;

using Agents.CodeReviewer;
using DevAgent.Contracts.Jobs;
using Microsoft.Extensions.Options;
using Xunit;

public class CodeReviewerServiceTests
{
    private static (CodeReviewerService service, RecordingTrigger trigger) NewService(params string[] watched)
    {
        var trigger = new RecordingTrigger();
        var service = new CodeReviewerService(
            Options.Create(new CodeReviewerOptions { RepositoryKeys = watched.ToList() }),
            trigger);
        return (service, trigger);
    }

    [Fact]
    public async Task PR_on_an_unwatched_repository_is_refused()
    {
        var (service, trigger) = NewService("other");

        var result = await service.HandlePullRequestOpenedAsync("svc-a", "feature/x", 42);

        Assert.Equal(AgentJobStatus.Rejected, result.Status);
        Assert.Empty(trigger.Requests);
    }

    [Fact]
    public async Task PR_on_a_watched_repository_becomes_a_review_proposal()
    {
        var (service, trigger) = NewService("svc-a");

        var result = await service.HandlePullRequestOpenedAsync("svc-a", "feature/x", 42);

        var request = Assert.Single(trigger.Requests);
        Assert.Equal("svc-a", request.RepositoryKey);
        Assert.Equal("feature/x", request.SourceBranch);
        Assert.Equal(42, request.PrNumber);
        Assert.Equal("CodeReviewer", request.RequestedBy);
        Assert.Equal(AgentJobType.CodeReview, request.JobType);
        Assert.NotEqual(AgentJobStatus.Rejected, result.Status);
    }

    [Fact]
    public async Task A_missing_pr_number_is_passed_through_as_null()
    {
        var (service, trigger) = NewService("svc-a");

        await service.HandlePullRequestOpenedAsync("svc-a", "feature/x", prNumber: null);

        Assert.Null(Assert.Single(trigger.Requests).PrNumber);
    }

    private sealed class RecordingTrigger : ICodeReviewTrigger
    {
        public List<CodeReviewJobRequest> Requests { get; } = new();

        public Task<AgentJobResult> StartCodeReviewAsync(CodeReviewJobRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new AgentJobResult { JobId = request.JobId, Status = AgentJobStatus.Succeeded });
        }
    }
}
