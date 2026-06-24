namespace DevAgent.Guard.Execution;

using System.Diagnostics;
using DevAgent.Contracts.Validation;
using DevAgent.Guard.Paths;
using DevAgent.Guard.Policies;

/// <summary>Outcome of a safely-executed command.</summary>
public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Abstraction over actually launching a process, so the runner can be tested
/// without spawning real processes.
/// </summary>
public interface IProcessExecutor
{
    Task<CommandResult> ExecuteAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken);
}

/// <summary>
/// Executes ONLY allowlisted commands, with arguments passed as a vector
/// (never a single string), inside a validated working directory.
///
/// SECURITY — this type is the single chokepoint for process execution in the
/// platform. Its guarantees:
///   * The executable + sub-command must pass <see cref="CommandPolicy"/>.
///   * Arguments are passed as a list to ArgumentList — NO shell is invoked,
///     so ";", "&amp;&amp;", "|", "$()" etc. are inert.
///   * The working directory must resolve inside the workspace.
/// There is deliberately no overload that accepts a raw command string.
/// </summary>
public sealed class SafeCommandRunner
{
    private readonly CommandPolicy _commandPolicy;
    private readonly WorkspacePathValidator _pathValidator;
    private readonly IProcessExecutor _executor;

    public SafeCommandRunner(
        CommandPolicy commandPolicy,
        WorkspacePathValidator pathValidator,
        IProcessExecutor? executor = null)
    {
        _commandPolicy = commandPolicy;
        _pathValidator = pathValidator;
        _executor = executor ?? new DefaultProcessExecutor();
    }

    /// <summary>
    /// Validates and runs a command. <paramref name="workingSubPath"/> is a
    /// workspace-relative directory (use "" for the workspace root).
    /// </summary>
    public async Task<CommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingSubPath = "",
        CancellationToken cancellationToken = default)
    {
        // 1. Command allowlist + argument sanity.
        _commandPolicy.Validate(executable, arguments).EnsureValid();

        // 2. Working directory must be inside the workspace.
        var workingDirectory = workingSubPath.Length == 0
            ? _pathValidator.WorkspaceRoot
            : _pathValidator.ResolveInsideWorkspace(workingSubPath);

        // 3. Execute as an argument vector — no shell interpretation.
        return await _executor.ExecuteAsync(executable, arguments, workingDirectory, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Validate without executing (useful for pre-flight checks/tests).</summary>
    public ValidationResult Validate(string executable, IReadOnlyList<string> arguments) =>
        _commandPolicy.Validate(executable, arguments);
}

/// <summary>
/// Default executor using <see cref="Process"/>. Note: UseShellExecute is false
/// and arguments are added individually to ArgumentList, so the OS receives a
/// proper argument vector and never a shell-interpreted string.
/// </summary>
public sealed class DefaultProcessExecutor : IProcessExecutor
{
    public async Task<CommandResult> ExecuteAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,   // SECURITY: never go through a shell.
            CreateNoWindow = true,
        };

        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        return new CommandResult(process.ExitCode, stdout, stderr);
    }
}
