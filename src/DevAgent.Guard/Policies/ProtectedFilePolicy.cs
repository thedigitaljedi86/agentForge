namespace DevAgent.Guard.Policies;

using DevAgent.Contracts.Validation;

/// <summary>
/// Rules describing files that must not be modified by the worker or a coding
/// agent. Two categories:
///   * SECRET files — never editable under any circumstances.
///   * DEPLOYMENT files — editable only when policy explicitly allows it.
///
/// SECURITY: This is enforced in addition to "no edits outside the workspace".
/// Even inside the workspace, these files are off-limits because modifying them
/// could leak secrets or silently change how the system is deployed.
/// </summary>
public sealed class ProtectedFilePolicy
{
    // Secret material — always protected.
    private static readonly string[] DefaultSecretSegments =
    {
        ".env",
        "secrets.json",
        "appsettings.production.json",
        "appsettings.secrets.json",
        ".pfx",
        ".pem",
        ".key",
        "id_rsa",
        ".npmrc",
        ".pgpass",
    };

    // Deployment / infrastructure descriptors — protected unless explicitly allowed.
    private static readonly string[] DefaultDeploymentSegments =
    {
        "dockerfile",
        "docker-compose",
        ".tf",
        ".tfvars",
        "deployment.yaml",
        "deployment.yml",
        "/.github/workflows/",
        "/k8s/",
        "/helm/",
        "/charts/",
    };

    private readonly string[] _secretSegments;
    private readonly string[] _deploymentSegments;

    public ProtectedFilePolicy()
        : this(DefaultSecretSegments, DefaultDeploymentSegments) { }

    public ProtectedFilePolicy(
        IEnumerable<string> secretSegments,
        IEnumerable<string> deploymentSegments)
    {
        _secretSegments = secretSegments.Select(Normalize).ToArray();
        _deploymentSegments = deploymentSegments.Select(Normalize).ToArray();
    }

    /// <summary>True if the path holds secret material — never editable.</summary>
    public bool IsSecretFile(string relativePath) => MatchesAny(relativePath, _secretSegments);

    /// <summary>True if the path is a deployment/infra descriptor.</summary>
    public bool IsDeploymentFile(string relativePath) => MatchesAny(relativePath, _deploymentSegments);

    /// <summary>True if the path is protected for any reason.</summary>
    public bool IsProtected(string relativePath) =>
        IsSecretFile(relativePath) || IsDeploymentFile(relativePath);

    /// <summary>
    /// Validates whether a file may be edited. Secrets are always rejected;
    /// deployment files are rejected unless <paramref name="allowDeploymentEdits"/>
    /// is true.
    /// </summary>
    public ValidationResult ValidateEditable(string relativePath, bool allowDeploymentEdits = false)
    {
        if (IsSecretFile(relativePath))
        {
            return ValidationResult.Fail($"File '{relativePath}' is a protected secret file and cannot be modified.");
        }

        if (IsDeploymentFile(relativePath) && !allowDeploymentEdits)
        {
            return ValidationResult.Fail(
                $"File '{relativePath}' is a deployment file; editing requires an explicit policy allowance.");
        }

        return ValidationResult.Success;
    }

    private static bool MatchesAny(string relativePath, string[] segments)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        // Prepend a separator so directory segments such as "/.github/workflows/"
        // match even when the supplied path has no leading slash.
        var normalized = "/" + Normalize(relativePath);
        foreach (var segment in segments)
        {
            if (normalized.Contains(segment, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string Normalize(string path) =>
        path.Replace('\\', '/').Trim().ToLowerInvariant();
}
