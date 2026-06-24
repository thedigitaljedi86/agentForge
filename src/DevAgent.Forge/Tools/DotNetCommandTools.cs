namespace DevAgent.Forge.Tools;

using DevAgent.Guard.Execution;

/// <summary>
/// Build / test / git-status tools. Each one routes through the
/// <see cref="SafeCommandRunner"/>, so the ONLY executables that can run are
/// the allowlisted ones (dotnet, git) with allowlisted sub-commands, as an
/// argument vector with no shell. The LLM cannot pass extra arguments beyond a
/// workspace-relative project/solution path, which is itself path-validated.
/// </summary>
public sealed class DotNetCommandTools
{
    private readonly SafeCommandRunner _runner;

    public DotNetCommandTools(SafeCommandRunner runner)
    {
        _runner = runner;
    }

    public async Task<ToolCallResult> BuildAsync(RunBuildToolCall call, CancellationToken ct = default)
    {
        // The project/solution selector is treated as a workspace-relative
        // working directory, so the SafeCommandRunner's path validator confines
        // it to the workspace (no traversal via the argument).
        var result = await _runner.RunAsync(
            "dotnet", new[] { "build" }, workingSubPath: call.ProjectOrSolution, cancellationToken: ct)
            .ConfigureAwait(false);

        return ToToolResult(call, result);
    }

    public async Task<ToolCallResult> TestAsync(RunTestToolCall call, CancellationToken ct = default)
    {
        var result = await _runner.RunAsync(
            "dotnet", new[] { "test" }, workingSubPath: call.ProjectOrSolution, cancellationToken: ct)
            .ConfigureAwait(false);

        return ToToolResult(call, result);
    }

    public async Task<ToolCallResult> GitStatusAsync(GitStatusToolCall call, CancellationToken ct = default)
    {
        var result = await _runner.RunAsync("git", new[] { "status", "--porcelain" }, cancellationToken: ct)
            .ConfigureAwait(false);
        return ToToolResult(call, result);
    }

    private static ToolCallResult ToToolResult(ToolCallRequest call, CommandResult result)
    {
        var combined = string.IsNullOrEmpty(result.StandardError)
            ? result.StandardOutput
            : result.StandardOutput + "\n" + result.StandardError;

        return new ToolCallResult
        {
            ToolCallId = call.ToolCallId,
            ToolName = call.ToolName,
            Success = result.Succeeded,
            Output = combined,
            Error = result.Succeeded ? null : $"Exit code {result.ExitCode}.",
        };
    }
}
