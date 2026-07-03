namespace DevAgent.Worker.DotNet.Tests;

using DevAgent.Bridge.Git;
using DevAgent.Contracts.Jobs;
using DevAgent.Forge;
using DevAgent.Guard.Execution;
using DevAgent.Guard.Paths;
using DevAgent.Guard.Policies;
using DevAgent.Worker.DotNet;
using Xunit;

/// <summary>
/// The three new in-sandbox flows:
///  * PipelineFix — no PR when the failure cannot be reproduced or when the
///    repair produced no real change; fails safely without an LLM.
///  * DocUpdate — the deterministic CODEMAP step runs without any LLM, the
///    build is re-verified, and a PR opens only when files changed.
///  * CodeReview — never commits/pushes; the review comment is the only output.
/// </summary>
public class AgentTaskWorkerTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "devagent-tasks-" + Guid.NewGuid().ToString("N"));
    private readonly string _repo;

    public AgentTaskWorkerTests()
    {
        // git clone is faked, so the workspace must already contain the repo.
        _repo = Path.Combine(_workspace, "repo");
        Directory.CreateDirectory(_repo);
        File.WriteAllText(Path.Combine(_repo, "App.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net8.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best effort */ }
    }

    private (SafeCommandRunner Runner, WorkspacePathValidator Paths) Guards(IProcessExecutor exec)
    {
        var paths = new WorkspacePathValidator(_workspace);
        return (new SafeCommandRunner(new CommandPolicy(), paths, exec), paths);
    }

    // ---- PipelineFix ----

    private PipelineFixWorkerSettings FixSettings() => new()
    {
        JobId = "fix-1",
        CloneUrl = "https://git.internal/x.git",
        BaseBranch = "feature/broken",
        WorkspaceRoot = _workspace,
        GitToken = "bot-token",
        FailureContext = "error CS1002: ; expected",
    };

    [Fact]
    public async Task PipelineFix_reports_NoChange_when_the_failure_does_not_reproduce()
    {
        // Everything is green locally -> nothing to fix, no PR.
        var exec = new ScriptedExecutor((_, _) => new CommandResult(0, "ok", ""));
        var (runner, paths) = Guards(exec);
        var agent = new StubCodingAgent("should not be called");

        var worker = new PipelineFixWorker(runner, paths, new PlaceholderGitProvider(), _ => agent);
        var result = await worker.RunAsync(FixSettings());

        Assert.Equal(AgentJobStatus.NoChange, result.Status);
        Assert.False(agent.WasCalled);
        Assert.Null(result.PullRequestUrl);
    }

    [Fact]
    public async Task PipelineFix_fails_safely_when_no_llm_is_configured()
    {
        var exec = new ScriptedExecutor((exe, args) =>
            exe == "dotnet" && args.Count > 0 && args[0] == "build"
                ? new CommandResult(1, "", "error CS1002")
                : new CommandResult(0, "ok", ""));
        var (runner, paths) = Guards(exec);

        var worker = new PipelineFixWorker(runner, paths, new PlaceholderGitProvider(), repairAgentFactory: null);
        var result = await worker.RunAsync(FixSettings());

        Assert.Equal(AgentJobStatus.Failed, result.Status);
        Assert.Null(result.PullRequestUrl);
        Assert.Contains("no LLM", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PipelineFix_repairs_reverifies_and_opens_a_PR_only_with_real_changes()
    {
        var buildCount = 0;
        var pushed = false;
        var exec = new ScriptedExecutor((exe, args) =>
        {
            if (exe == "dotnet" && args.Count > 0 && args[0] == "build")
            {
                buildCount++;
                // Broken on arrival, green after the agent's edit.
                return buildCount == 1 ? new CommandResult(1, "", "error CS1002") : new CommandResult(0, "ok", "");
            }
            if (exe == "git" && args.Count > 0 && args[0] == "status")
            {
                return new CommandResult(0, " M App.cs\n", "");
            }
            if (exe == "git" && args.Count > 0 && args[0] == "push")
            {
                pushed = true;
            }
            return new CommandResult(0, "ok", "");
        });
        var (runner, paths) = Guards(exec);
        var agent = new StubCodingAgent("Fixed the missing semicolon.");

        var worker = new PipelineFixWorker(runner, paths, new PlaceholderGitProvider(), _ => agent);
        var result = await worker.RunAsync(FixSettings());

        Assert.True(agent.WasCalled);
        Assert.Contains("error CS1002", agent.SeenFailureContext); // CI log reached the agent as context
        Assert.Equal(AgentJobStatus.Succeeded, result.Status);
        Assert.True(pushed);
        Assert.NotNull(result.PullRequestUrl);
    }

    [Fact]
    public async Task PipelineFix_opens_no_PR_when_the_repair_changed_nothing()
    {
        var buildCount = 0;
        var pushed = false;
        var exec = new ScriptedExecutor((exe, args) =>
        {
            if (exe == "dotnet" && args.Count > 0 && args[0] == "build")
            {
                buildCount++;
                return buildCount == 1 ? new CommandResult(1, "", "error CS1002") : new CommandResult(0, "ok", "");
            }
            if (exe == "git" && args.Count > 0 && args[0] == "status")
            {
                return new CommandResult(0, "", ""); // clean tree: no real change
            }
            if (exe == "git" && args.Count > 0 && args[0] == "push")
            {
                pushed = true;
            }
            return new CommandResult(0, "ok", "");
        });
        var (runner, paths) = Guards(exec);

        var worker = new PipelineFixWorker(runner, paths, new PlaceholderGitProvider(), _ => new StubCodingAgent("did nothing"));
        var result = await worker.RunAsync(FixSettings());

        Assert.Equal(AgentJobStatus.NoChange, result.Status);
        Assert.False(pushed);
        Assert.Null(result.PullRequestUrl);
    }

    // ---- DocUpdate ----

    private DocUpdateWorkerSettings DocSettings() => new()
    {
        JobId = "docs-1",
        CloneUrl = "https://git.internal/x.git",
        BaseBranch = "main",
        WorkspaceRoot = _workspace,
        GitToken = "bot-token",
    };

    [Fact]
    public async Task DocUpdate_generates_CODEMAP_without_an_llm_and_opens_a_PR()
    {
        var exec = new ScriptedExecutor((exe, args) =>
            exe == "git" && args.Count > 0 && args[0] == "status"
                ? new CommandResult(0, "?? docs/CODEMAP.md\n", "")
                : new CommandResult(0, "ok", ""));
        var (runner, paths) = Guards(exec);

        var worker = new DocScribeWorker(runner, paths, new PlaceholderGitProvider(), authoringAgentFactory: null);
        var result = await worker.RunAsync(DocSettings());

        Assert.Equal(AgentJobStatus.Succeeded, result.Status);
        Assert.NotNull(result.PullRequestUrl);

        var codemap = File.ReadAllText(Path.Combine(_repo, "docs", "CODEMAP.md"));
        Assert.Contains("App.csproj", codemap);
        Assert.Contains("net8.0", codemap);
    }

    [Fact]
    public async Task DocUpdate_refuses_a_PR_when_the_build_is_no_longer_green()
    {
        var exec = new ScriptedExecutor((exe, args) =>
            exe == "dotnet" && args.Count > 0 && args[0] == "build"
                ? new CommandResult(1, "", "error CS0246")
                : new CommandResult(0, "ok", ""));
        var (runner, paths) = Guards(exec);

        var worker = new DocScribeWorker(runner, paths, new PlaceholderGitProvider(), authoringAgentFactory: null);
        var result = await worker.RunAsync(DocSettings());

        Assert.Equal(AgentJobStatus.Failed, result.Status);
        Assert.Null(result.PullRequestUrl);
    }

    [Fact]
    public async Task DocUpdate_reports_NoChange_when_docs_are_already_current()
    {
        // First run writes CODEMAP.md into the repo…
        new DocInventory().GenerateCodeMap(_repo);

        // …second run over an unchanged repo produces no tree changes.
        var exec = new ScriptedExecutor((exe, args) =>
            exe == "git" && args.Count > 0 && args[0] == "status"
                ? new CommandResult(0, "", "")
                : new CommandResult(0, "ok", ""));
        var (runner, paths) = Guards(exec);

        var worker = new DocScribeWorker(runner, paths, new PlaceholderGitProvider(), authoringAgentFactory: null);
        var result = await worker.RunAsync(DocSettings());

        Assert.Equal(AgentJobStatus.NoChange, result.Status);
        Assert.Null(result.PullRequestUrl);
    }

    // ---- CodeReview ----

    private CodeReviewWorkerSettings ReviewSettings(int? prNumber = 42) => new()
    {
        JobId = "review-1",
        CloneUrl = "https://git.internal/x.git",
        BaseBranch = "main",
        SourceBranch = "feature/change",
        WorkspaceRoot = _workspace,
        GitToken = "bot-token",
        PrNumber = prNumber,
    };

    [Fact]
    public async Task CodeReview_never_pushes_and_posts_the_review_as_a_comment()
    {
        var pushedOrCommitted = false;
        var exec = new ScriptedExecutor((exe, args) =>
        {
            if (exe == "git" && args.Count > 0 && (args[0] == "push" || args[0] == "commit"))
            {
                pushedOrCommitted = true;
            }
            if (exe == "git" && args.Count > 0 && args[0] == "diff")
            {
                return new CommandResult(0, "diff --git a/App.cs b/App.cs\n+var x = 1;\n", "");
            }
            return new CommandResult(0, "ok", "");
        });
        var (runner, paths) = Guards(exec);
        var git = new RecordingGitProvider();
        var agent = new StubCodingAgent("Looks correct; consider adding a test for the new branch.");

        var worker = new CodeReviewWorker(runner, paths, git, _ => agent);
        var result = await worker.RunAsync(ReviewSettings());

        Assert.Equal(AgentJobStatus.Succeeded, result.Status);
        Assert.False(pushedOrCommitted);
        Assert.True(agent.WasCalled);
        Assert.Contains("diff --git", agent.SeenFailureContext); // the PR diff reached the agent as context
        Assert.Equal(42, git.CommentPrNumber);
        Assert.Contains("adding a test", git.CommentBody);
        Assert.Null(result.PullRequestUrl); // reviews never open PRs
    }

    [Fact]
    public async Task CodeReview_fails_safely_without_an_llm()
    {
        var exec = new ScriptedExecutor((_, _) => new CommandResult(0, "ok", ""));
        var (runner, paths) = Guards(exec);

        var worker = new CodeReviewWorker(runner, paths, new PlaceholderGitProvider(), reviewAgentFactory: null);
        var result = await worker.RunAsync(ReviewSettings());

        Assert.Equal(AgentJobStatus.Failed, result.Status);
        Assert.Contains("No LLM", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CodeReview_reports_NoChange_for_an_empty_diff()
    {
        var exec = new ScriptedExecutor((exe, args) =>
            exe == "git" && args.Count > 0 && args[0] == "diff"
                ? new CommandResult(0, "", "")
                : new CommandResult(0, "ok", ""));
        var (runner, paths) = Guards(exec);
        var agent = new StubCodingAgent("unused");

        var worker = new CodeReviewWorker(runner, paths, new PlaceholderGitProvider(), _ => agent);
        var result = await worker.RunAsync(ReviewSettings());

        Assert.Equal(AgentJobStatus.NoChange, result.Status);
        Assert.False(agent.WasCalled);
    }

    // ---- settings from environment ----

    [Fact]
    public void PipelineFix_settings_fail_safely_when_required_variables_are_missing()
    {
        var ex = Assert.Throws<MissingWorkerConfigurationException>(() =>
            PipelineFixWorkerSettings.FromEnvironment(_ => null));
        Assert.Contains(WorkerJobSettings.JobIdVar, ex.MissingVariables);
        Assert.Contains(WorkerJobSettings.GitTokenVar, ex.MissingVariables);
    }

    [Fact]
    public void CodeReview_settings_require_the_source_branch_and_parse_the_pr_number()
    {
        var env = new Dictionary<string, string>
        {
            [WorkerJobSettings.JobIdVar] = "j1",
            [WorkerJobSettings.CloneUrlVar] = "https://git.internal/x.git",
            [WorkerJobSettings.BaseBranchVar] = "main",
            [WorkerJobSettings.SourceBranchVar] = "feature/x",
            [WorkerJobSettings.WorkspaceRootVar] = "/workspace",
            [WorkerJobSettings.GitTokenVar] = "t",
            [WorkerJobSettings.PrNumberVar] = "17",
        };
        var settings = CodeReviewWorkerSettings.FromEnvironment(k => env.GetValueOrDefault(k));
        Assert.Equal("feature/x", settings.SourceBranch);
        Assert.Equal(17, settings.PrNumber);

        env.Remove(WorkerJobSettings.SourceBranchVar);
        var ex = Assert.Throws<MissingWorkerConfigurationException>(() =>
            CodeReviewWorkerSettings.FromEnvironment(k => env.GetValueOrDefault(k)));
        Assert.Contains(WorkerJobSettings.SourceBranchVar, ex.MissingVariables);
    }

    // --- fakes ---

    private sealed class ScriptedExecutor : IProcessExecutor
    {
        private readonly Func<string, IReadOnlyList<string>, CommandResult> _respond;
        public ScriptedExecutor(Func<string, IReadOnlyList<string>, CommandResult> respond) => _respond = respond;

        public Task<CommandResult> ExecuteAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken ct)
            => Task.FromResult(_respond(executable, arguments));
    }

    private sealed class StubCodingAgent : ICodingAgent
    {
        private readonly string _summary;
        public bool WasCalled { get; private set; }
        public string SeenFailureContext { get; private set; } = string.Empty;

        public StubCodingAgent(string summary) => _summary = summary;

        public Task<CodingAgentResult> RunAsync(CodingAgentTask task, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            SeenFailureContext = task.FailureContext ?? string.Empty;
            return Task.FromResult(new CodingAgentResult
            {
                JobId = task.JobId,
                Succeeded = true,
                ReasoningSummary = _summary,
            });
        }
    }

    private sealed class RecordingGitProvider : IGitProvider
    {
        public int? CommentPrNumber { get; private set; }
        public string CommentBody { get; private set; } = string.Empty;

        public Task<GitRepository> GetRepositoryAsync(string cloneUrl, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitRepository { CloneUrl = cloneUrl, DefaultBranch = "main" });

        public Task<PullRequestResult> CreatePullRequestAsync(GitRepository repository, PullRequestRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PullRequestResult { Created = true, Url = "pr://test" });

        public Task<PullRequestResult> PostPullRequestCommentAsync(GitRepository repository, int prNumber, string comment, CancellationToken cancellationToken = default)
        {
            CommentPrNumber = prNumber;
            CommentBody = comment;
            return Task.FromResult(new PullRequestResult { Created = true, Url = $"pr://test/comment/{prNumber}" });
        }
    }
}
