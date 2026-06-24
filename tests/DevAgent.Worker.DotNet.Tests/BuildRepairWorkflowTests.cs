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
/// Exercises the opt-in Forge build-repair step end-to-end through the
/// DotNetUpgradeWorker: a framework bump breaks the build, the controlled coding
/// agent gets one attempt, the worker re-verifies and (only then) opens a
/// review-required PR. Without the agent the same broken build fails safely.
/// </summary>
public class BuildRepairWorkflowTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "devagent-repair-" + Guid.NewGuid().ToString("N"));

    public BuildRepairWorkflowTests()
    {
        // Pre-create the cloned "repo" dir with a project on an old framework.
        // (git clone is faked, so the workspace must already contain the repo.)
        var repo = Path.Combine(_workspace, "repo");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "App.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net6.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best effort */ }
    }

    private DotNetUpgradeWorkerSettings Settings() => new()
    {
        JobId = "job-1",
        CloneUrl = "https://git.internal/x.git",
        BaseBranch = "main",
        TargetFramework = "net8.0",
        WorkspaceRoot = _workspace,
        GitToken = "bot-token",
    };

    private DotNetUpgradeWorker NewWorker(IProcessExecutor exec, Func<string, ICodingAgent>? repair)
    {
        var paths = new WorkspacePathValidator(_workspace);
        var runner = new SafeCommandRunner(new CommandPolicy(), paths, exec);
        return new DotNetUpgradeWorker(runner, paths, new TargetFrameworkUpdater(), new PlaceholderGitProvider(), repair);
    }

    [Fact]
    public async Task Failing_build_is_repaired_by_the_agent_then_a_PR_is_opened()
    {
        var buildCount = 0;
        var exec = new ScriptedExecutor((exe, args) =>
        {
            if (exe == "dotnet" && args.Count > 0 && args[0] == "build")
            {
                buildCount++;
                return buildCount == 1 ? new CommandResult(1, "", "error CS1002") : new CommandResult(0, "Build succeeded", "");
            }
            return new CommandResult(0, "ok", "");
        });

        var agent = new StubCodingAgent(succeeded: true, summary: "Adjusted target-framework-specific API usage.");
        var result = await NewWorker(exec, _ => agent).RunAsync(Settings());

        Assert.True(agent.WasCalled);
        Assert.Equal(AgentJobStatus.Succeeded, result.Status);
        Assert.True(result.BuildSucceeded);
        Assert.NotNull(result.PullRequestUrl);
        Assert.Contains("repaired", result.Message!, StringComparison.OrdinalIgnoreCase);

        // The deterministic upgrade still happened on disk.
        Assert.Contains("net8.0", File.ReadAllText(Path.Combine(_workspace, "repo", "App.csproj")));
    }

    [Fact]
    public async Task Without_a_repair_agent_a_broken_build_fails_safely_with_no_PR()
    {
        var exec = new ScriptedExecutor((exe, args) =>
            exe == "dotnet" && args.Count > 0 && args[0] == "build"
                ? new CommandResult(1, "", "error CS1002")
                : new CommandResult(0, "ok", ""));

        var result = await NewWorker(exec, repair: null).RunAsync(Settings());

        Assert.Equal(AgentJobStatus.Failed, result.Status);
        Assert.False(result.BuildSucceeded);
        Assert.Null(result.PullRequestUrl);
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
        private readonly bool _succeeded;
        private readonly string _summary;
        public bool WasCalled { get; private set; }

        public StubCodingAgent(bool succeeded, string summary)
        {
            _succeeded = succeeded;
            _summary = summary;
        }

        public Task<CodingAgentResult> RunAsync(CodingAgentTask task, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult(new CodingAgentResult
            {
                JobId = task.JobId,
                Succeeded = _succeeded,
                ReasoningSummary = _summary,
            });
        }
    }
}
