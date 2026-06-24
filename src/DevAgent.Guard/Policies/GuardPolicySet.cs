namespace DevAgent.Guard.Policies;

/// <summary>
/// Convenience aggregate bundling the allowlist policies that the Runner needs
/// to validate a job. Keeping them together makes it obvious that ALL of these
/// checks are part of one security gate.
/// </summary>
public sealed class GuardPolicySet
{
    public required RepositoryPolicy Repositories { get; init; }
    public required PackagePolicy Packages { get; init; }
    public required ContainerImagePolicy ContainerImages { get; init; }
    public required JobPolicy JobTypes { get; init; }
    public ProtectedFilePolicy ProtectedFiles { get; init; } = new();
    public CommandPolicy Commands { get; init; } = new();
}
