namespace DevAgent.Guard.Policies;

using DevAgent.Contracts.Validation;

/// <summary>
/// A single allowlisted repository entry. Repositories are referenced by
/// <see cref="Key"/> everywhere in the platform; the concrete clone URL is
/// only ever resolved here, never supplied by a caller.
/// </summary>
public sealed record RepositoryEntry
{
    public required string Key { get; init; }
    public required string CloneUrl { get; init; }
    public string BaseBranch { get; init; } = "main";
}

/// <summary>
/// Allowlist of repositories the platform is permitted to operate on.
///
/// SECURITY: There is no method that accepts a raw URL. The only way to obtain
/// a clone URL is to resolve a key that an administrator added to the allowlist.
/// This is the single defence against SSRF / arbitrary-repo cloning.
/// </summary>
public sealed class RepositoryPolicy
{
    private readonly IReadOnlyDictionary<string, RepositoryEntry> _byKey;

    public RepositoryPolicy(IEnumerable<RepositoryEntry> allowedRepositories)
    {
        _byKey = allowedRepositories.ToDictionary(
            r => r.Key,
            StringComparer.OrdinalIgnoreCase);
    }

    public bool IsAllowed(string repositoryKey) =>
        !string.IsNullOrWhiteSpace(repositoryKey) && _byKey.ContainsKey(repositoryKey);

    public ValidationResult Validate(string repositoryKey) =>
        IsAllowed(repositoryKey)
            ? ValidationResult.Success
            : ValidationResult.Fail($"Repository '{repositoryKey}' is not on the allowlist.");

    /// <summary>
    /// Resolves a key to a trusted repository entry. Throws a policy violation
    /// rather than returning null so a missing key can never be silently
    /// turned into an arbitrary URL downstream.
    /// </summary>
    public RepositoryEntry Resolve(string repositoryKey)
    {
        if (!_byKey.TryGetValue(repositoryKey, out var entry))
        {
            throw new PolicyViolationException($"Repository '{repositoryKey}' is not on the allowlist.");
        }

        return entry;
    }
}
