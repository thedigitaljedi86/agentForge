namespace DevAgent.Runner.Tests;

using DevAgent.Audit;
using DevAgent.Contracts.Jobs;
using DevAgent.Contracts.Sandbox;
using DevAgent.Guard.Policies;
using DevAgent.Runner.Api.Application;
using DevAgent.Runner.Api.Sandbox;
using Xunit;

/// <summary>
/// The Runner's allowlist gate for DotNetUpgrade jobs: a valid key + framework is
/// resolved to a trusted URL + image and dispatched; bad repo / framework / job
/// type are rejected before any sandbox runs.
/// </summary>
public class RunnerDotNetUpgradeTests
{
    private const string Image = "registry/worker:8.0";

    private static GuardPolicySet Policies(
        bool repoAllowed = true,
        bool jobTypeAllowed = true,
        IEnumerable<string>? frameworks = null)
    {
        var repos = new RepositoryPolicy(repoAllowed
            ? new[] { new RepositoryEntry { Key = "svc-a", CloneUrl = "https://git/svc-a.git", BaseBranch = "main" } }
            : Array.Empty<RepositoryEntry>());

        var jobTypes = new JobPolicy(jobTypeAllowed
            ? new Dictionary<AgentJobType, string> { [AgentJobType.DotNetUpgrade] = Image }
            : new Dictionary<AgentJobType, string>());

        return new GuardPolicySet
        {
            Repositories = repos,
            Packages = new PackagePolicy(Array.Empty<string>()),
            JobTypes = jobTypes,
            ContainerImages = new ContainerImagePolicy(new[] { Image }),
            TargetFrameworks = new TargetFrameworkPolicy(frameworks ?? new[] { "net8.0" }),
        };
    }

    private static (RunnerJobApplicationService service, RecordingSandboxRunner runner) NewService(GuardPolicySet policies)
    {
        var runner = new RecordingSandboxRunner();
        return (new RunnerJobApplicationService(policies, runner, new ConsoleAuditLog()), runner);
    }

    private static DotNetUpgradeJobRequest ValidRequest() => new()
    {
        RepositoryKey = "svc-a",
        TargetFramework = "net8.0",
    };

    [Fact]
    public async Task Valid_request_is_dispatched_with_resolved_values()
    {
        var (service, runner) = NewService(Policies());

        var result = await service.StartDotNetUpgradeAsync(ValidRequest());

        Assert.NotEqual(AgentJobStatus.Rejected, result.Status);
        Assert.NotNull(runner.LastRequest);
        Assert.Equal(AgentJobType.DotNetUpgrade, runner.LastRequest!.JobType);
        Assert.Equal("https://git/svc-a.git", runner.LastRequest.CloneUrl);
        Assert.Equal(Image, runner.LastRequest.ContainerImage);
        Assert.Equal("net8.0", runner.LastRequest.TargetFramework);
    }

    [Fact]
    public async Task Repository_must_be_allowlisted()
    {
        var (service, runner) = NewService(Policies(repoAllowed: false));
        var result = await service.StartDotNetUpgradeAsync(ValidRequest());

        Assert.Equal(AgentJobStatus.Rejected, result.Status);
        Assert.Null(runner.LastRequest);
    }

    [Fact]
    public async Task Job_type_must_be_allowlisted()
    {
        var (service, runner) = NewService(Policies(jobTypeAllowed: false));
        var result = await service.StartDotNetUpgradeAsync(ValidRequest());

        Assert.Equal(AgentJobStatus.Rejected, result.Status);
        Assert.Null(runner.LastRequest);
    }

    [Theory]
    [InlineData("net7.0")]   // well-formed but not on the allowlist
    [InlineData("net48")]    // not a modern moniker
    [InlineData("garbage")]  // not a framework at all
    public async Task Target_framework_must_pass_policy(string framework)
    {
        var (service, runner) = NewService(Policies(frameworks: new[] { "net8.0" }));
        var result = await service.StartDotNetUpgradeAsync(ValidRequest() with { TargetFramework = framework });

        Assert.Equal(AgentJobStatus.Rejected, result.Status);
        Assert.Null(runner.LastRequest);
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
                Status = AgentJobStatus.Validated,
                PullRequestUrl = "https://git/pr/2",
            });
        }
    }
}
