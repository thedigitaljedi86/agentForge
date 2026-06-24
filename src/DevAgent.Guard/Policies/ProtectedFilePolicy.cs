namespace DevAgent.Guard.Policies;

using DevAgent.Contracts.Validation;

/// <summary>
/// Rules describing files that must never be created, edited or deleted by
/// the worker or a future coding agent — secrets and deployment descriptors.
///
/// SECURITY: This is enforced in addition to "no edits outside the workspace".
/// Even inside the workspace, certain files are off-limits because modifying
/// them could leak secrets or alter deployment behaviour.
/// </summary>
public sealed class ProtectedFilePolicy
{
    // Glob-ish suffixes / names that are protected by default. Matching is on
    // the normalised, lower-cased relative path.
    private static readonly string[] DefaultProtectedSegments =
    {
        // Secret material
        ".env",
        "secrets.json",
        "appsettings.production.json",
        "appsettings.secrets.json",
        ".pfx",
        ".pem",
        ".key",
        "id_rsa",
        // Deployment / infrastructure descriptors
        "dockerfile",
        "docker-compose",
        ".tf",            // terraform
        ".tfvars",
        "deployment.yaml",
        "deployment.yml",
        "/.github/workflows/", // CI pipelines
        "/k8s/",
        "/helm/",
    };

    private readonly string[] _protectedSegments;

    public ProtectedFilePolicy() : this(DefaultProtectedSegments) { }

    public ProtectedFilePolicy(IEnumerable<string> protectedSegments)
    {
        _protectedSegments = protectedSegments.Select(Normalize).ToArray();
    }

    /// <summary>
    /// Returns true if the given workspace-relative path is protected and must
    /// not be modified.
    /// </summary>
    public bool IsProtected(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        // Prepend a separator so directory segments such as "/.github/workflows/"
        // match even when the supplied path has no leading slash.
        var normalized = "/" + Normalize(relativePath);
        foreach (var segment in _protectedSegments)
        {
            if (normalized.Contains(segment, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public ValidationResult ValidateEditable(string relativePath) =>
        IsProtected(relativePath)
            ? ValidationResult.Fail($"File '{relativePath}' is protected and cannot be modified.")
            : ValidationResult.Success;

    private static string Normalize(string path) =>
        path.Replace('\\', '/').Trim().ToLowerInvariant();
}
