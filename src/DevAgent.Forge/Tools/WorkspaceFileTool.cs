namespace DevAgent.Forge.Tools;

using DevAgent.Guard.Paths;
using DevAgent.Guard.Policies;

/// <summary>
/// File tools (list / read / replace) confined to the workspace.
///
/// SECURITY: Every path is validated by <see cref="WorkspacePathValidator"/>
/// (no traversal, no absolute paths, no escaping the repo) and every mutation
/// is checked against <see cref="ProtectedFilePolicy"/> (secrets never; deploy
/// files only when allowed). Reads of secret files are also blocked so the
/// agent cannot exfiltrate them into its context.
/// </summary>
public sealed class WorkspaceFileTool
{
    private readonly WorkspacePathValidator _paths;
    private readonly ProtectedFilePolicy _protected;
    private readonly bool _allowDeploymentEdits;
    private readonly WriteScopePolicy _writeScope;

    public WorkspaceFileTool(
        WorkspacePathValidator paths,
        ProtectedFilePolicy protectedFiles,
        bool allowDeploymentEdits,
        WriteScopePolicy? writeScope = null)
    {
        _paths = paths;
        _protected = protectedFiles;
        _allowDeploymentEdits = allowDeploymentEdits;
        _writeScope = writeScope ?? WriteScopePolicy.AllowAll;
    }

    public Task<ToolCallResult> ListAsync(ListFilesToolCall call, CancellationToken ct = default)
    {
        var pathCheck = _paths.Validate(call.RelativePath.Length == 0 ? "." : call.RelativePath);
        if (call.RelativePath.Length > 0 && !pathCheck.IsValid)
        {
            return Task.FromResult(ToolCallResult.Denied(call, pathCheck.Reason!));
        }

        var root = call.RelativePath.Length == 0
            ? _paths.WorkspaceRoot
            : _paths.ResolveInsideWorkspace(call.RelativePath);

        if (!Directory.Exists(root))
        {
            return Task.FromResult(ToolCallResult.Fail(call, $"Directory '{call.RelativePath}' does not exist."));
        }

        var option = call.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var entries = Directory.EnumerateFileSystemEntries(root, "*", option)
            .Select(p => Path.GetRelativePath(_paths.WorkspaceRoot, p).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal);

        return Task.FromResult(ToolCallResult.Ok(call, string.Join('\n', entries)));
    }

    public async Task<ToolCallResult> ReadAsync(ReadFileToolCall call, CancellationToken ct = default)
    {
        var pathCheck = _paths.Validate(call.RelativePath);
        if (!pathCheck.IsValid)
        {
            return ToolCallResult.Denied(call, pathCheck.Reason!);
        }

        // Do not let the agent read secret material into its context.
        if (_protected.IsSecretFile(call.RelativePath))
        {
            return ToolCallResult.Denied(call, $"Reading secret file '{call.RelativePath}' is not permitted.");
        }

        var full = _paths.ResolveInsideWorkspace(call.RelativePath);
        if (!File.Exists(full))
        {
            return ToolCallResult.Fail(call, $"File '{call.RelativePath}' does not exist.");
        }

        var content = await File.ReadAllTextAsync(full, ct).ConfigureAwait(false);
        return ToolCallResult.Ok(call, content);
    }

    public async Task<ToolCallResult> ReplaceAsync(ReplaceFileToolCall call, CancellationToken ct = default)
    {
        var guard = ValidateMutable(call.RelativePath);
        if (guard is not null)
        {
            return ToolCallResult.Denied(call, guard);
        }

        var full = _paths.ResolveInsideWorkspace(call.RelativePath);
        var before = File.Exists(full) ? await File.ReadAllTextAsync(full, ct).ConfigureAwait(false) : string.Empty;

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, call.NewContent, ct).ConfigureAwait(false);

        return ToolCallResult.Ok(call) with
        {
            ChangedFile = call.RelativePath,
            Diff = UnifiedDiff.Create(call.RelativePath, before, call.NewContent),
        };
    }

    /// <summary>Returns a denial reason, or null when the path may be edited.</summary>
    private string? ValidateMutable(string relativePath)
    {
        var pathCheck = _paths.Validate(relativePath);
        if (!pathCheck.IsValid)
        {
            return pathCheck.Reason;
        }

        // The agent's write scope (e.g. docs-only, or read-only) applies to
        // every mutation, on top of the protected-file rules.
        var scope = _writeScope.ValidateWrite(relativePath);
        if (!scope.IsValid)
        {
            return scope.Reason;
        }

        var editable = _protected.ValidateEditable(relativePath, _allowDeploymentEdits);
        return editable.IsValid ? null : editable.Reason;
    }
}
