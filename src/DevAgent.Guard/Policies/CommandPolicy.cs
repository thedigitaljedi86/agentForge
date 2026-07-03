namespace DevAgent.Guard.Policies;

using DevAgent.Contracts.Validation;

/// <summary>
/// Allowlist of executables the <see cref="DevAgent.Guard.Execution.SafeCommandRunner"/>
/// is permitted to launch, plus the allowed first-argument (sub-command) set
/// for each.
///
/// SECURITY: This is the heart of "no arbitrary command execution". Only the
/// executables listed here can run, and we additionally constrain their
/// sub-commands so e.g. "git" cannot be used to run an arbitrary alias or
/// "dotnet" cannot invoke "dotnet exec" on a random assembly. There is no
/// shell involved — commands are launched as argument vectors, so shell
/// metacharacters (;, &amp;&amp;, |, $()) carry no special meaning.
/// </summary>
public sealed class CommandPolicy
{
    // First milestone: only dotnet and git, with a tight sub-command allowlist.
    private static readonly IReadOnlyDictionary<string, HashSet<string>> DefaultAllowed =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["dotnet"] = new(StringComparer.Ordinal)
            {
                "restore", "build", "test", "add", "list", "--version",
            },
            ["git"] = new(StringComparer.Ordinal)
            {
                "clone", "checkout", "switch", "add", "commit", "push", "config", "status", "rev-parse",
                "fetch", "diff",
            },
        };

    private readonly IReadOnlyDictionary<string, HashSet<string>> _allowed;

    public CommandPolicy() : this(DefaultAllowed) { }

    public CommandPolicy(IReadOnlyDictionary<string, HashSet<string>> allowed)
    {
        _allowed = allowed;
    }

    /// <summary>The set of executables this policy permits (e.g. dotnet, git).</summary>
    public IReadOnlyCollection<string> AllowedExecutables => (IReadOnlyCollection<string>)_allowed.Keys;

    /// <summary>
    /// Validates a fully-parsed command (executable + argument vector). The
    /// executable must be allowlisted and, where sub-commands are constrained,
    /// the first argument must be in the allowed set.
    /// </summary>
    public ValidationResult Validate(string executable, IReadOnlyList<string> arguments)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return ValidationResult.Fail("No executable supplied.");
        }

        // Reject any attempt to smuggle a path (./evil, /usr/bin/x, ..\x).
        if (executable.IndexOfAny(new[] { '/', '\\' }) >= 0)
        {
            return ValidationResult.Fail($"Executable '{executable}' must be a bare command name, not a path.");
        }

        if (!_allowed.TryGetValue(executable, out var allowedSubcommands))
        {
            return ValidationResult.Fail($"Executable '{executable}' is not on the command allowlist.");
        }

        // If sub-commands are constrained for this executable, enforce them.
        if (allowedSubcommands.Count > 0)
        {
            var first = arguments.Count > 0 ? arguments[0] : null;
            if (first is null || !allowedSubcommands.Contains(first))
            {
                return ValidationResult.Fail(
                    $"Sub-command '{first ?? "<none>"}' is not allowed for '{executable}'.");
            }
        }

        // Defence in depth: reject shell metacharacters anywhere in arguments.
        foreach (var arg in arguments)
        {
            if (ContainsShellMetacharacters(arg))
            {
                return ValidationResult.Fail($"Argument '{arg}' contains disallowed shell metacharacters.");
            }
        }

        return ValidationResult.Success;
    }

    private static bool ContainsShellMetacharacters(string value)
    {
        // We never run through a shell, but rejecting these is cheap defence in
        // depth and surfaces obviously-malicious input early.
        foreach (var c in value)
        {
            if (c is ';' or '|' or '&' or '$' or '`' or '\n' or '\r' or '<' or '>')
            {
                return true;
            }
        }

        return false;
    }
}
