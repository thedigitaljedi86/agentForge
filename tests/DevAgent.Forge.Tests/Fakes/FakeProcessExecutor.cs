namespace DevAgent.Forge.Tests.Fakes;

using DevAgent.Guard.Execution;

/// <summary>
/// Fake process executor that records invocations and returns scripted results,
/// so build/test tools can be tested without a real dotnet/git on the machine.
/// </summary>
public sealed class FakeProcessExecutor : IProcessExecutor
{
    private readonly Func<string, IReadOnlyList<string>, CommandResult> _respond;

    public List<(string Exe, IReadOnlyList<string> Args, string WorkingDir)> Invocations { get; } = new();

    public FakeProcessExecutor(Func<string, IReadOnlyList<string>, CommandResult>? respond = null)
    {
        _respond = respond ?? ((_, _) => new CommandResult(0, "ok", ""));
    }

    public Task<CommandResult> ExecuteAsync(
        string executable, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken ct)
    {
        Invocations.Add((executable, arguments, workingDirectory));
        return Task.FromResult(_respond(executable, arguments));
    }
}
