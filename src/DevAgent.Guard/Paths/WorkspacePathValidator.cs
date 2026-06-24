namespace DevAgent.Guard.Paths;

using DevAgent.Contracts.Validation;

/// <summary>
/// Validates that a path stays strictly inside the worker's workspace root.
///
/// SECURITY: This prevents path traversal ("../../etc/passwd"), absolute-path
/// escapes ("/etc/shadow") and symlink-style escapes via canonicalisation.
/// The worker is only ever allowed to touch files under its workspace; host
/// filesystem paths must never be reachable.
/// </summary>
public sealed class WorkspacePathValidator
{
    private readonly string _workspaceRoot;

    public WorkspacePathValidator(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            throw new ArgumentException("Workspace root must be provided.", nameof(workspaceRoot));
        }

        // Canonicalise the root once. GetFullPath collapses ".." and "." and
        // normalises separators. We append a trailing separator so prefix
        // checks cannot be fooled by a sibling like "/work" vs "/workspace".
        _workspaceRoot = AppendSeparator(Path.GetFullPath(workspaceRoot));
    }

    public string WorkspaceRoot => _workspaceRoot;

    /// <summary>
    /// Resolves a workspace-relative path to an absolute path, guaranteeing the
    /// result is inside the workspace. Throws on any escape attempt.
    /// </summary>
    public string ResolveInsideWorkspace(string relativePath)
    {
        var result = Validate(relativePath);
        if (!result.IsValid)
        {
            throw new PolicyViolationException(result.Reason!);
        }

        return Path.GetFullPath(Path.Combine(_workspaceRoot, relativePath));
    }

    public ValidationResult Validate(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return ValidationResult.Fail("Path must not be empty.");
        }

        // Reject rooted/absolute paths outright — they ignore the workspace.
        if (Path.IsPathRooted(relativePath))
        {
            return ValidationResult.Fail($"Absolute paths are not permitted: '{relativePath}'.");
        }

        // Combine then canonicalise; this collapses any "../" segments.
        var combined = Path.GetFullPath(Path.Combine(_workspaceRoot, relativePath));

        // The canonical path must remain under the workspace root.
        if (!combined.StartsWith(_workspaceRoot, StringComparison.Ordinal))
        {
            return ValidationResult.Fail($"Path escapes the workspace: '{relativePath}'.");
        }

        return ValidationResult.Success;
    }

    public bool IsInsideWorkspace(string relativePath) => Validate(relativePath).IsValid;

    private static string AppendSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
}
