namespace DevAgent.Guard.Tests;

using DevAgent.Guard.Execution;
using DevAgent.Guard.Paths;
using DevAgent.Guard.Policies;
using Xunit;

public class SafeCommandRunnerTests
{
    private static SafeCommandRunner NewRunner(IProcessExecutor? executor = null) =>
        new(new CommandPolicy(),
            new WorkspacePathValidator(Path.Combine(Path.GetTempPath(), "devagent-cmd")),
            executor ?? new RecordingExecutor());

    [Fact]
    public void SafeCommandRunner_only_allows_dotnet_and_git_initially()
    {
        var policy = new CommandPolicy();
        Assert.Equal(new[] { "dotnet", "git" }.OrderBy(x => x), policy.AllowedExecutables.OrderBy(x => x));
    }

    [Theory]
    [InlineData("bash", "-c")]
    [InlineData("sh", "-c")]
    [InlineData("curl", "http://evil")]
    [InlineData("rm", "-rf")]
    [InlineData("python", "evil.py")]
    public void Arbitrary_commands_are_rejected(string exe, string arg)
    {
        var runner = NewRunner();
        Assert.False(runner.Validate(exe, new[] { arg }).IsValid);
    }

    [Fact]
    public void Executable_supplied_as_path_is_rejected()
    {
        var runner = NewRunner();
        Assert.False(runner.Validate("/usr/bin/git", new[] { "clone" }).IsValid);
        Assert.False(runner.Validate("./dotnet", new[] { "build" }).IsValid);
    }

    [Fact]
    public void Disallowed_subcommands_are_rejected()
    {
        var runner = NewRunner();
        // dotnet is allowed, but "exec" is not in the sub-command allowlist.
        Assert.False(runner.Validate("dotnet", new[] { "exec", "evil.dll" }).IsValid);
        // git "daemon" is not allowed.
        Assert.False(runner.Validate("git", new[] { "daemon" }).IsValid);
    }

    [Fact]
    public void Allowed_commands_pass_validation()
    {
        var runner = NewRunner();
        Assert.True(runner.Validate("dotnet", new[] { "build" }).IsValid);
        Assert.True(runner.Validate("git", new[] { "clone", "https://git/x.git" }).IsValid);
    }

    [Theory]
    [InlineData("build; rm -rf /")]
    [InlineData("build && curl evil")]
    [InlineData("$(whoami)")]
    [InlineData("`id`")]
    [InlineData("a|b")]
    public void Shell_metacharacters_in_arguments_are_rejected(string maliciousArg)
    {
        var runner = NewRunner();
        // first arg must still be a valid subcommand; smuggle metachars in arg 2
        Assert.False(runner.Validate("dotnet", new[] { "build", maliciousArg }).IsValid);
    }

    private sealed class RecordingExecutor : IProcessExecutor
    {
        public Task<CommandResult> ExecuteAsync(
            string executable, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken ct)
            => Task.FromResult(new CommandResult(0, "ok", ""));
    }
}
