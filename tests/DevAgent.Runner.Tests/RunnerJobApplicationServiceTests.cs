namespace DevAgent.Runner.Tests;

using DevAgent.Audit;
using DevAgent.Contracts.Jobs;
using DevAgent.Contracts.Sandbox;
using DevAgent.Guard.Policies;
using DevAgent.Runner.Api.Application;
using DevAgent.Runner.Api.Sandbox;
using Xunit;

public class RunnerJobApplicationServiceTests
{
    private const string Image = "registry/worker:8.0";

    private static GuardPolicySet Policies(
        bool repoAllowed = true,
        bool packageAllowed = true,
        bool jobTypeAllowed = true,
        bool imageAllowed = true)
    {
        var repos = new RepositoryPolicy(repoAllowed
            ? new[] { new RepositoryEntry { Key = "svc-a", CloneUrl = "https://git/svc-a.git", BaseBranch = "main" } }
            : Array.Empty<RepositoryEntry>());

        var packages = new PackagePolicy(packageAllowed ? new[] { "Serilog" } : Array.Empty<string>());

        var jobTypes = new JobPolicy(jobTypeAllowed
            ? new Dictionary<AgentJobType, string> { [AgentJobType.NuGetUpdate] = Image }
            : new Dictionary<AgentJobType, string>());

        var images = new ContainerImagePolicy(imageAllowed ? new[] { Image } : Array.Empty<string>());

        return new GuardPolicySet
        {
            Repositories = repos,
            Packages = packages,
            JobTypes = jobTypes,
            ContainerImages = images,
        };
    }

    private static (RunnerJobApplicationService service, RecordingSandboxRunner runner) NewService(GuardPolicySet policies)
    {
        var runner = new RecordingSandboxRunner();
        var service = new RunnerJobApplicationService(new StaticGuardPolicySource(policies), runner, new ConsoleAuditLog());
        return (service, runner);
    }

    private static NuGetUpdateJobRequest ValidRequest() => new()
    {
        RepositoryKey = "svc-a",
        PackageId = "Serilog",
        TargetVersion = "3.1.1",
    };

    [Fact]
    public async Task Valid_request_is_dispatched_to_sandbox_with_resolved_values()
    {
        var (service, runner) = NewService(Policies());

        var result = await service.StartNuGetUpdateAsync(ValidRequest());

        Assert.NotEqual(AgentJobStatus.Rejected, result.Status);
        Assert.NotNull(runner.LastRequest);
        // The caller passed a KEY; the runner received a RESOLVED url + image.
        Assert.Equal("https://git/svc-a.git", runner.LastRequest!.CloneUrl);
        Assert.Equal(Image, runner.LastRequest.ContainerImage);
    }

    [Fact]
    public async Task Repository_must_be_allowlisted()
    {
        var (service, runner) = NewService(Policies(repoAllowed: false));
        var result = await service.StartNuGetUpdateAsync(ValidRequest());

        Assert.Equal(AgentJobStatus.Rejected, result.Status);
        Assert.Null(runner.LastRequest); // never reached the sandbox
    }

    [Fact]
    public async Task Package_must_be_allowlisted()
    {
        var (service, runner) = NewService(Policies(packageAllowed: false));
        var result = await service.StartNuGetUpdateAsync(ValidRequest());

        Assert.Equal(AgentJobStatus.Rejected, result.Status);
        Assert.Null(runner.LastRequest);
    }

    [Fact]
    public async Task Job_type_must_be_allowlisted()
    {
        var (service, runner) = NewService(Policies(jobTypeAllowed: false));
        var result = await service.StartNuGetUpdateAsync(ValidRequest());

        Assert.Equal(AgentJobStatus.Rejected, result.Status);
        Assert.Null(runner.LastRequest);
    }

    [Fact]
    public async Task No_auto_merge_behavior_exists()
    {
        // The runner result type cannot express a merge; success surfaces a PR
        // URL only. This test documents/locks that invariant.
        var (service, _) = NewService(Policies());
        var result = await service.StartNuGetUpdateAsync(ValidRequest());

        var statusName = result.Status.ToString();
        Assert.DoesNotContain("merge", statusName, StringComparison.OrdinalIgnoreCase);
        // AgentJobResult has no Merge/AutoMerge member.
        Assert.Null(typeof(AgentJobResult).GetProperty("AutoMerge"));
        Assert.Null(typeof(AgentJobResult).GetProperty("Merged"));
    }

    private sealed class RecordingSandboxRunner : ISandboxJobRunner
    {
        public SandboxJobRequest? LastRequest { get; private set; }

        public Task<SandboxJobResult> RunAsync(SandboxJobRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(new SandboxJobResult
            {
                JobId = request.JobId,
                Status = AgentJobStatus.Succeeded,
                PullRequestUrl = "https://git/pr/1",
            });
        }
    }
}
