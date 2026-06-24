namespace DevAgent.Guard.Policies;

using DevAgent.Contracts.Validation;

/// <summary>
/// Allowlist of container images a sandbox worker may run in.
///
/// SECURITY: Callers never supply an image. The Runner picks an image for a
/// job type from this allowlist. This prevents running untrusted images that
/// could carry tooling to escape the sandbox or exfiltrate data.
/// </summary>
public sealed class ContainerImagePolicy
{
    private readonly HashSet<string> _allowed;

    public ContainerImagePolicy(IEnumerable<string> allowedImages)
    {
        _allowed = new HashSet<string>(allowedImages, StringComparer.Ordinal);
    }

    public bool IsAllowed(string image) =>
        !string.IsNullOrWhiteSpace(image) && _allowed.Contains(image);

    public ValidationResult Validate(string image) =>
        IsAllowed(image)
            ? ValidationResult.Success
            : ValidationResult.Fail($"Container image '{image}' is not on the allowlist.");
}
