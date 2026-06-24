namespace DevAgent.Guard.Policies;

using System.Text.RegularExpressions;
using DevAgent.Contracts.Validation;

/// <summary>
/// Validates the target framework requested by a .NET-upgrade job.
///
/// SECURITY: A caller-supplied framework string must (a) be a well-formed modern
/// SDK-style moniker (<c>net&lt;major&gt;.&lt;minor&gt;</c>) and (b) when an
/// administrator has configured an allowlist, be on it. This stops a trigger from
/// pushing an arbitrary or malformed framework value into a worker. When no
/// allowlist is configured, only the format check applies.
/// </summary>
public sealed class TargetFrameworkPolicy
{
    private static readonly Regex Modern = new(@"^net\d+\.\d+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IReadOnlySet<string> _allowed;

    /// <summary>Format-only policy (no explicit allowlist).</summary>
    public TargetFrameworkPolicy() : this(Array.Empty<string>()) { }

    public TargetFrameworkPolicy(IEnumerable<string> allowed)
    {
        _allowed = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
    }

    public bool IsAllowed(string targetFramework) =>
        !string.IsNullOrWhiteSpace(targetFramework)
        && Modern.IsMatch(targetFramework)
        && (_allowed.Count == 0 || _allowed.Contains(targetFramework));

    public ValidationResult Validate(string targetFramework) =>
        IsAllowed(targetFramework)
            ? ValidationResult.Success
            : ValidationResult.Fail($"Target framework '{targetFramework}' is not allowed.");
}
