namespace DevAgent.Forge.Tests;

using DevAgent.Forge;
using DevAgent.Forge.Tests.Fakes;
using DevAgent.Forge.Tools;
using DevAgent.Guard.Execution;
using DevAgent.Guard.Paths;
using DevAgent.Guard.Policies;
using Xunit;

public class DotNetCommandToolsTests
{
    private static (DotNetCommandTools tools, FakeProcessExecutor exec) NewTools(
        Func<string, IReadOnlyList<string>, CommandResult>? respond = null)
    {
        var exec = new FakeProcessExecutor(respond);
        var paths = new WorkspacePathValidator(Path.Combine(Path.GetTempPath(), "devagent-cmdtool"));
        var runner = new SafeCommandRunner(new CommandPolicy(), paths, exec);
        return (new DotNetCommandTools(runner), exec);
    }

    [Fact]
    public async Task Build_invokes_dotnet_build()
    {
        var (tools, exec) = NewTools();
        var result = await tools.BuildAsync(new RunBuildToolCall());

        Assert.True(result.Success);
        var call = Assert.Single(exec.Invocations);
        Assert.Equal("dotnet", call.Exe);
        Assert.Equal("build", call.Args[0]);
    }

    [Fact]
    public async Task Test_invokes_dotnet_test()
    {
        var (tools, exec) = NewTools();
        await tools.TestAsync(new RunTestToolCall());

        var call = Assert.Single(exec.Invocations);
        Assert.Equal("dotnet", call.Exe);
        Assert.Equal("test", call.Args[0]);
    }

    [Fact]
    public async Task GitStatus_invokes_git_status()
    {
        var (tools, exec) = NewTools();
        await tools.GitStatusAsync(new GitStatusToolCall());

        var call = Assert.Single(exec.Invocations);
        Assert.Equal("git", call.Exe);
        Assert.Equal("status", call.Args[0]);
    }

    [Fact]
    public async Task Failing_build_surfaces_error_and_output()
    {
        var (tools, _) = NewTools((_, _) => new CommandResult(1, "build output", "CS1002 error"));
        var result = await tools.BuildAsync(new RunBuildToolCall());

        Assert.False(result.Success);
        Assert.Contains("CS1002", result.Output);
    }

    [Fact]
    public async Task Command_tools_only_ever_invoke_dotnet_or_git()
    {
        var (tools, exec) = NewTools();
        await tools.BuildAsync(new RunBuildToolCall());
        await tools.TestAsync(new RunTestToolCall());
        await tools.GitStatusAsync(new GitStatusToolCall());

        Assert.All(exec.Invocations, i => Assert.Contains(i.Exe, new[] { "dotnet", "git" }));
    }
}
