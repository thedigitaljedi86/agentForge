namespace DevAgent.Guard.Policies;

using DevAgent.Contracts.Validation;

/// <summary>
/// Restricts WHERE an agent may write inside the workspace — on top of the
/// workspace confinement and the protected-file rules, never instead of them.
///
/// Three shapes:
///   * <see cref="AllowAll"/>  — the default for code-repair agents.
///   * <see cref="FromPrefixes"/> — e.g. ["docs/", "README.md"]: DocScribe can
///     maintain documentation but is STRUCTURALLY unable to touch code.
///   * <see cref="ReadOnly"/> — CodeReviewer can never write anything.
///
/// SECURITY: This is a policy object, not a prompt instruction. It is enforced
/// in the file/patch tools before any write, and denials are audited like
/// every other policy decision.
/// </summary>
public sealed class WriteScopePolicy
{
    private readonly IReadOnlyList<string>? _prefixes; // null = allow all

    private WriteScopePolicy(IReadOnlyList<string>? prefixes)
    {
        _prefixes = prefixes;
    }

    /// <summary>No write restriction beyond workspace + protected files.</summary>
    public static WriteScopePolicy AllowAll { get; } = new(null);

    /// <summary>No writes at all (review/analysis agents).</summary>
    public static WriteScopePolicy ReadOnly { get; } = new(Array.Empty<string>());

    /// <summary>Writes allowed only under the given workspace-relative prefixes (or exact files).</summary>
    public static WriteScopePolicy FromPrefixes(IEnumerable<string> prefixes) =>
        new(prefixes.Select(Normalize).ToArray());

    public ValidationResult ValidateWrite(string relativePath)
    {
        if (_prefixes is null)
        {
            return ValidationResult.Success;
        }

        if (_prefixes.Count == 0)
        {
            return ValidationResult.Fail("This agent is read-only; writes are not permitted.");
        }

        var normalized = Normalize(relativePath);
        foreach (var prefix in _prefixes)
        {
            // A prefix matches a directory subtree ("docs/") or an exact file
            // ("readme.md").
            if (normalized.StartsWith(prefix, StringComparison.Ordinal) || normalized == prefix.TrimEnd('/'))
            {
                return ValidationResult.Success;
            }
        }

        return ValidationResult.Fail(
            $"Write to '{relativePath}' is outside this agent's write scope ({string.Join(", ", _prefixes)}).");
    }

    private static string Normalize(string path) =>
        path.Replace('\\', '/').TrimStart('/').Trim().ToLowerInvariant();
}
