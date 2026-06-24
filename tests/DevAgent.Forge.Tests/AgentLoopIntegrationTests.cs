namespace DevAgent.Forge.Tests;

using DevAgent.Forge;
using DevAgent.Forge.Tests.Fakes;
using DevAgent.Guard.Execution;
using Xunit;

public class AgentLoopIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devagent-agent-" + Guid.NewGuid().ToString("N"));

    public AgentLoopIntegrationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void WriteFile(string rel, string content)
    {
        var full = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private CodingAgentTask Task() => new()
    {
        JobId = "job-1",
        Goal = "Fix the failing build by correcting Add().",
        WorkspaceRoot = _root,
        FailureContext = "Add returns a - b but tests expect a + b.",
    };

    [Fact]
    public async Task Agent_reads_patches_and_rebuilds_then_completes()
    {
        WriteFile("src/Program.cs", "class P {\n  static int Add(int a, int b) => a - b;\n}\n");

        // First build fails, second build (after patch) succeeds.
        var buildCount = 0;
        var exec = new FakeProcessExecutor((exe, args) =>
        {
            if (args.Count > 0 && args[0] == "build")
            {
                buildCount++;
                return buildCount == 1
                    ? new CommandResult(1, "", "error CS0000")
                    : new CommandResult(0, "Build succeeded", "");
            }
            return new CommandResult(0, "", "");
        });

        var patch = "@@ -2,1 +2,1 @@\n-  static int Add(int a, int b) => a - b;\n+  static int Add(int a, int b) => a + b;\n";

        var llm = new ScriptedLlmClient(new[]
        {
            new LlmDecision { Reasoning = "inspect the file", ToolCall = new ReadFileToolCall { RelativePath = "src/Program.cs" } },
            new LlmDecision { Reasoning = "build to see the failure", ToolCall = new RunBuildToolCall() },
            new LlmDecision { Reasoning = "fix the operator", ToolCall = new ApplyPatchToolCall { RelativePath = "src/Program.cs", UnifiedDiff = patch } },
            new LlmDecision { Reasoning = "rebuild", ToolCall = new RunBuildToolCall() },
            new LlmDecision { IsComplete = true, Summary = "Changed subtraction to addition in Add()." },
        });

        var audit = new RecordingAuditLog();
        var agent = CodingAgentFactory.Create(_root, llm, audit, "job-1",
            options: new CodingAgentOptions { MaxIterations = 10 },
            processExecutor: exec);

        var result = await agent.RunAsync(Task());

        Assert.True(result.Succeeded);
        Assert.False(result.StoppedAtIterationLimit);
        Assert.Equal("Changed subtraction to addition in Add().", result.ReasoningSummary);
        Assert.Contains("src/Program.cs", result.ChangedFiles);
        Assert.False(string.IsNullOrEmpty(result.FinalDiff));
        Assert.Equal(4, result.Steps.Count); // read, build, patch, build

        // The file on disk was actually fixed.
        var onDisk = File.ReadAllText(Path.Combine(_root, "src/Program.cs"));
        Assert.Contains("a + b", onDisk);

        // Every tool call was audited, and the diff was saved.
        Assert.Equal(4, audit.ToolCalls.Count());
        Assert.NotEmpty(audit.Diffs);
        Assert.Contains(audit.Prompts, p => p.Prompt.Contains("fix the operator"));
    }

    [Fact]
    public async Task Agent_stops_at_iteration_limit_when_model_never_completes()
    {
        WriteFile("src/Program.cs", "class P {}\n");

        // Empty script -> ScriptedLlmClient keeps returning git_status forever.
        var llm = new ScriptedLlmClient(Array.Empty<LlmDecision>());
        var audit = new RecordingAuditLog();
        var agent = CodingAgentFactory.Create(_root, llm, audit, "job-1",
            options: new CodingAgentOptions { MaxIterations = 3 },
            processExecutor: new FakeProcessExecutor());

        var result = await agent.RunAsync(Task());

        Assert.True(result.StoppedAtIterationLimit);
        Assert.False(result.Succeeded);
        Assert.Equal(3, result.IterationsUsed);
        Assert.Equal(3, result.Steps.Count);
    }

    [Fact]
    public async Task Agent_run_is_unsuccessful_when_a_tool_is_denied()
    {
        WriteFile(".env", "SECRET=1");

        var llm = new ScriptedLlmClient(new[]
        {
            // The model tries to read a secret file — must be denied.
            new LlmDecision { Reasoning = "peek at secrets", ToolCall = new ReadFileToolCall { RelativePath = ".env" } },
            new LlmDecision { IsComplete = true, Summary = "done" },
        });

        var audit = new RecordingAuditLog();
        var agent = CodingAgentFactory.Create(_root, llm, audit, "job-1",
            processExecutor: new FakeProcessExecutor());

        var result = await agent.RunAsync(Task());

        Assert.False(result.Succeeded);
        Assert.Contains(result.Steps, s => s.Result.DeniedByPolicy);
        Assert.Contains(audit.ToolCalls, t => !t.Allowed);
    }
}
