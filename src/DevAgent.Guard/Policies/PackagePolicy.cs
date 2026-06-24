namespace DevAgent.Guard.Policies;

using DevAgent.Contracts.Validation;

/// <summary>
/// Allowlist of NuGet package ids the platform may update.
///
/// SECURITY: Restricting which packages can be updated limits blast radius —
/// a compromised trigger cannot push updates for an arbitrary package the org
/// has not vetted. Comparison is case-insensitive (NuGet ids are).
/// </summary>
public sealed class PackagePolicy
{
    private readonly HashSet<string> _allowed;

    public PackagePolicy(IEnumerable<string> allowedPackageIds)
    {
        _allowed = new HashSet<string>(allowedPackageIds, StringComparer.OrdinalIgnoreCase);
    }

    public bool IsAllowed(string packageId) =>
        !string.IsNullOrWhiteSpace(packageId) && _allowed.Contains(packageId);

    public ValidationResult Validate(string packageId) =>
        IsAllowed(packageId)
            ? ValidationResult.Success
            : ValidationResult.Fail($"Package '{packageId}' is not on the allowlist.");
}
