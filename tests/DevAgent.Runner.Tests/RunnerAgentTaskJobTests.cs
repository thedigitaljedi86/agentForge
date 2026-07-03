namespace DevAgent.Runner.Tests;

using DevAgent.Audit;
using DevAgent.Contracts.Jobs;
using DevAgent.Contracts.Sandbox;
using DevAgent.Guard.Policies;
using DevAgent.Runner.Api.Application;
using DevAgent.Runner.Api.Sandbox;
using Xunit;

/// <summary>
/// Gate tests for the three task-style jobs (PipelineFix, DocUpdate,
/// CodeReview): keys resolve to trusted values, external branch names must
/// pass the conservative ref-name filter, and the failure log is truncated
/// before it reaches the sandbox environment.
/// </summary>
public class RunnerAgentTaskJobTests
{
    private const string Image = "registry/worker:8.0";

    private static GuardPolicySet Policies(bool jobTypesAllowed = true)
    {
        var jobTypes = jobTypesAllowed
            ? new Dictionary<AgentJobType, string>
            {
                [AgentJobType.PipelineFix] = Image,
                [AgentJobType.DocUpdate] = Image,
                [AgentJobType.CodeReview] = Image,
            }
            : new Dictionary<AgentJobType, string>();

        return new GuardPolicySet
        {
            Repositories = new RepositoryPolicy(new[]
            {
                new RepositoryEntry { Key = "svc-a", CloneUrl = "https://git/svc-a.git", BaseBranch = "main" },
            }),
            Packages = new PackagePolicy(Array.Empty<string>()),
            JobTypes = new JobPolicy(jobTypes),
            ContainerImages = new ContainerImagePolicy(new[] { Image }),
        };
    }

    private static (RunnerJobApplicationService service, RecordingSandboxRunner runner) NewService(GuardPolicySet? policies = null)
    {
        var runner = new RecordingSandboxRunner();
        var service = new RunnerJobApplicationService(new StaticGuardPolicySource(policies ?? Policies()), runner, new ConsoleAuditLog());
        return (service, runner);
    }

    // ---- PipelineFix ----

    [Fact]
    public async Task PipelineFix_dispatches_from_the_failing_branch_with_resolved_values()
    {
        var (service, runner) = NewService();

        var result = await service.StartPipelineFixAsync(new PipelineFixJobRequest
        {
            JobId = "j1",
            RepositoryKey = "svc-a",
            Branch = "feature/broken-ci",
            FailureContext = "error CS1002",
        });

        Assert.NotEqual(AgentJobStatus.Rejected, result.Status);
        Assert.Equal("https://git/svc-a.git", runner.LastRequest!.CloneUrl);
        Assert.Equal("feature/broken-ci", runner.LastRequest.BaseBranch); // failing branch, not repo default
        Assert.Equal("error CS1002", runner.LastRequest.FailureContext);
        Assert.Equal(Image, runner.LastRequest.ContainerImage);
    }

    [Theory]
    [InlineData("branch;rm -rf")]
    [InlineData("branch name")]
    [InlineData("branch\nother")]
    [InlineData("-b")]
    [InlineData("")]
    public async Task PipelineFix_rejects_invalid_branch_names(string branch)
    {
        var (service, runner) = NewService();

        var result = await service.StartPipelineFixAsync(new PipelineFixJobRequest
        {
            JobId = "j1",
            RepositoryKey = "svc-a",
            Branch = branch,
        });

        Assert.Equal(AgentJobStatus.Rejected, result.Status);
        Assert.Null(runner.LastRequest);
    }

    [Fact]
    public async Task PipelineFix_truncates_a_huge_failure_log_keeping_the_tail()
    {
        var (service, runner) = NewService();
        var log = new string('a', 50_000) + "THE-ACTUAL-ERROR";

        await service.StartPipelineFixAsync(new PipelineFixJobRequest
        {
            JobId = "j1",
            RepositoryKey = "svc-a",
            Branch = "main",
            FailureContext = log,
        });

        Assert.NotNull(runner.LastRequest);
        Assert.True(runner.LastRequest!.FailureContext!.Length <= 12_000);
        Assert.EndsWith("THE-ACTUAL-ERROR", runner.LastRequest.FailureContext);
    }

    [Fact]
    public async Task PipelineFix_requires_the_job_type_on_the_allowlist()
    {
        var (service, runner) = NewService(Policies(jobTypesAllowed: false));

        var result = await service.StartPipelineFixAsync(new PipelineFixJobRequest
        {
            JobId = "j1",
            RepositoryKey = "svc-a",
            Branch = "main",
        });

        Assert.Equal(AgentJobStatus.Rejected, result.Status);
        Assert.Null(runner.LastRequest);
    }

    // ---- DocUpdate ----

    [Fact]
    public async Task DocUpdate_dispatches_with_the_repo_default_branch()
    {
        var (service, runner) = NewService();

        var result = await service.StartDocUpdateAsync(new DocUpdateJobRequest
        {
            JobId = "j2",
            RepositoryKey = "svc-a",
        });

        Assert.NotEqual(AgentJobStatus.Rejected, result.Status);
        Assert.Equal("main", runner.LastRequest!.BaseBranch);
        Assert.Equal(AgentJobType.DocUpdate, runner.LastRequest.JobType);
    }

    [Fact]
    public async Task DocUpdate_rejects_an_unknown_repository_key()
    {
        var (service, runner) = NewService();

        var result = await service.StartDocUpdateAsync(new DocUpdateJobRequest
        {
            JobId = "j2",
            RepositoryKey = "not-allowlisted",
        });

        Assert.Equal(AgentJobStatus.Rejected, result.Status);
        Assert.Null(runner.LastRequest);
    }

    // ---- CodeReview ----

    [Fact]
    public async Task CodeReview_dispatches_with_source_branch_and_pr_number()
    {
        var (service, runner) = NewService();

        var result = await service.StartCodeReviewAsync(new CodeReviewJobRequest
        {
            JobId = "j3",
            RepositoryKey = "svc-a",
            SourceBranch = "feature/new-api",
            PrNumber = 42,
        });

        Assert.NotEqual(AgentJobStatus.Rejected, result.Status);
        Assert.Equal("main", runner.LastRequest!.BaseBranch);
        Assert.Equal("feature/new-api", runner.LastRequest.SourceBranch);
        Assert.Equal(42, runner.LastRequest.PrNumber);
    }

    [Theory]
    [InlineData("branch|pipe")]
    [InlineData("$(evil)")]
    [InlineData("-option")]
    public async Task CodeReview_rejects_invalid_source_branch_names(string branch)
    {
        var (service, runner) = NewService();

        var result = await service.StartCodeReviewAsync(new CodeReviewJobRequest
        {
            JobId = "j3",
            RepositoryKey = "svc-a",
            SourceBranch = branch,
        });

        Assert.Equal(AgentJobStatus.Rejected, result.Status);
        Assert.Null(runner.LastRequest);
    }

    // ---- ref-name policy unit coverage ----

    [Theory]
    [InlineData("main")]
    [InlineData("feature/JIRA-123_fix.v2")]
    [InlineData("release/1.0")]
    public void RefNamePolicy_accepts_conservative_names(string name) =>
        Assert.True(RefNamePolicy.Validate(name).IsValid);

    [Theory]
    [InlineData("a b")]
    [InlineData("a;b")]
    [InlineData("a`b")]
    [InlineData("-b")]
    [InlineData("")]
    [InlineData(null)]
    public void RefNamePolicy_rejects_dangerous_names(string? name) =>
        Assert.False(RefNamePolicy.Validate(name).IsValid);

    // ---- environment passing in the CLI launcher ----

    [Fact]
    public async Task Cli_launcher_passes_task_fields_as_environment_values()
    {
        var launcher = new RecordingLauncher();
        var runner = new CliSandboxJobRunner(
            new ContainerImagePolicy(new[] { Image }),
            new SandboxOptions { WorkerGitToken = "bot" },
            launcher,
            new ConsoleAuditLog());

        await runner.RunAsync(new SandboxJobRequest
        {
            JobId = "j4",
            JobType = AgentJobType.CodeReview,
            CloneUrl = "https://git/svc-a.git",
            BaseBranch = "main",
            ContainerImage = Image,
            SourceBranch = "feature/x",
            PrNumber = 7,
            FailureContext = "log tail",
        });

        var args = launcher.LastArgs!;
        Assert.Contains("DEVAGENT_SOURCE_BRANCH=feature/x", args);
        Assert.Contains("DEVAGENT_PR_NUMBER=7", args);
        Assert.Contains("DEVAGENT_FAILURE_CONTEXT=log tail", args);
        Assert.Equal(Image, args[^1]); // image stays the last argument
    }

    // ---- fakes ----

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
            });
        }
    }

    private sealed class RecordingLauncher : ISandboxProcessLauncher
    {
        public IReadOnlyList<string>? LastArgs { get; private set; }

        public Task<SandboxProcessResult> LaunchAsync(string cli, IReadOnlyList<string> arguments, CancellationToken ct)
        {
            LastArgs = arguments;
            return Task.FromResult(new SandboxProcessResult(0, "[worker] ok", ""));
        }
    }
}
