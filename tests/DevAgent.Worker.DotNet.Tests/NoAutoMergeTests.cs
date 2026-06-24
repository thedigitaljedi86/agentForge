namespace DevAgent.Worker.DotNet.Tests;

using DevAgent.Bridge.Git;
using DevAgent.Contracts.Validation;
using Xunit;

public class NoAutoMergeTests
{
    [Fact]
    public async Task Placeholder_provider_opens_a_pull_request_without_merging()
    {
        var provider = new PlaceholderGitProvider();
        var repo = await provider.GetRepositoryAsync("https://git/x.git");

        var result = await provider.CreatePullRequestAsync(repo, new PullRequestRequest
        {
            SourceBranch = "devagent/update",
            TargetBranch = "main",
            Title = "Update",
            AutoMerge = false,
        });

        Assert.True(result.Created);
        Assert.NotNull(result.Url);
    }

    [Fact]
    public async Task Auto_merge_request_is_refused()
    {
        var provider = new PlaceholderGitProvider();
        var repo = await provider.GetRepositoryAsync("https://git/x.git");

        await Assert.ThrowsAsync<PolicyViolationException>(() =>
            provider.CreatePullRequestAsync(repo, new PullRequestRequest
            {
                SourceBranch = "b",
                TargetBranch = "main",
                Title = "x",
                AutoMerge = true, // must be refused
            }));
    }

    [Fact]
    public void PullRequestRequest_defaults_to_no_auto_merge()
    {
        var request = new PullRequestRequest { SourceBranch = "b", TargetBranch = "main", Title = "x" };
        Assert.False(request.AutoMerge);
    }
}
