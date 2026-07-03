namespace DevAgent.Forge.Tests;

using DevAgent.Forge;
using DevAgent.Forge.Tests.Fakes;
using DevAgent.Forge.Tools;
using DevAgent.Guard.Paths;
using DevAgent.Guard.Policies;
using Xunit;

public class McpToolGateTests
{
    private sealed class RecordingExecutor : IMcpToolExecutor
    {
        public McpToolCall? LastCall { get; private set; }

        public Task<ToolCallResult> ExecuteAsync(McpToolCall call, CancellationToken ct = default)
        {
            LastCall = call;
            return Task.FromResult(ToolCallResult.Ok(call, "advisory: none"));
        }
    }

    private static CodingAgentToolHandler NewHandler(RecordingAuditLog audit, IMcpToolExecutor? mcp)
    {
        var root = Path.Combine(Path.GetTempPath(), "devagent-mcp-gate");
        Directory.CreateDirectory(root);
        var paths = new WorkspacePathValidator(root);
        var protectedFiles = new ProtectedFilePolicy();
        return new CodingAgentToolHandler(
            new ToolPolicy(),
            new WorkspaceFileTool(paths, protectedFiles, allowDeploymentEdits: false),
            new PatchApplicationService(paths, protectedFiles, allowDeploymentEdits: false),
            new DotNetCommandTools(new DevAgent.Guard.Execution.SafeCommandRunner(
                new CommandPolicy(), paths, new FakeProcessExecutor())),
            audit,
            "job-1",
            mcp);
    }

    private static McpToolCall Call() => new()
    {
        ServerKey = "advisories",
        Tool = "query",
        ArgumentsJson = """{"package":"Serilog"}""",
    };

    [Fact]
    public async Task Mcp_calls_are_denied_when_no_executor_is_configured()
    {
        var audit = new RecordingAuditLog();
        var handler = NewHandler(audit, mcp: null);

        var result = await handler.HandleAsync(Call());

        Assert.True(result.DeniedByPolicy);
        var logged = Assert.Single(audit.ToolCalls);
        Assert.False(logged.Allowed);
        Assert.Equal("mcp__advisories__query", logged.ToolName);
    }

    [Fact]
    public async Task Mcp_calls_are_routed_to_the_executor_and_audited()
    {
        var audit = new RecordingAuditLog();
        var executor = new RecordingExecutor();
        var handler = NewHandler(audit, executor);

        var result = await handler.HandleAsync(Call());

        Assert.True(result.Success);
        Assert.Equal("query", executor.LastCall!.Tool);
        var logged = Assert.Single(audit.ToolCalls);
        Assert.True(logged.Allowed);
        Assert.Contains("server='advisories'", logged.Arguments);
    }

    [Fact]
    public void Forbidden_names_are_still_forbidden_for_mcp_leaf_tools()
    {
        // Defence-in-depth helper used by executors: a granted MCP tool that
        // calls itself "bash" is refused by name alone.
        Assert.True(ToolPolicy.IsForbiddenName("bash"));
        Assert.True(ToolPolicy.IsForbiddenName("exec"));
        Assert.False(ToolPolicy.IsForbiddenName("query_advisories"));
    }
}
