namespace DevAgent.Forge.Tools;

using DevAgent.Guard.Paths;
using DevAgent.Guard.Policies;

/// <summary>
/// Applies an LLM-supplied unified diff to a single workspace file.
///
/// SECURITY: The target path is validated against the workspace
/// (<see cref="WorkspacePathValidator"/>) and the protected-file policy
/// (<see cref="ProtectedFilePolicy"/>) BEFORE any write. The patch is applied
/// in-process by <see cref="UnifiedDiff"/> — there is no "git apply" and no
/// shell. A patch that does not cleanly match the source is rejected, never
/// force-applied.
/// </summary>
public sealed class PatchApplicationService
{
    private readonly WorkspacePathValidator _paths;
    private readonly ProtectedFilePolicy _protected;
    private readonly bool _allowDeploymentEdits;
    private readonly WriteScopePolicy _writeScope;

    public PatchApplicationService(
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

    public async Task<ToolCallResult> ApplyAsync(ApplyPatchToolCall call, CancellationToken ct = default)
    {
        var pathCheck = _paths.Validate(call.RelativePath);
        if (!pathCheck.IsValid)
        {
            return ToolCallResult.Denied(call, pathCheck.Reason!);
        }

        var scope = _writeScope.ValidateWrite(call.RelativePath);
        if (!scope.IsValid)
        {
            return ToolCallResult.Denied(call, scope.Reason!);
        }

        var editable = _protected.ValidateEditable(call.RelativePath, _allowDeploymentEdits);
        if (!editable.IsValid)
        {
            return ToolCallResult.Denied(call, editable.Reason!);
        }

        var full = _paths.ResolveInsideWorkspace(call.RelativePath);
        var before = File.Exists(full) ? await File.ReadAllTextAsync(full, ct).ConfigureAwait(false) : string.Empty;

        var apply = UnifiedDiff.Apply(before, call.UnifiedDiff);
        if (!apply.Success)
        {
            return ToolCallResult.Fail(call, $"Patch did not apply: {apply.Error}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, apply.NewContent!, ct).ConfigureAwait(false);

        return ToolCallResult.Ok(call, $"Patched {call.RelativePath}.") with
        {
            ChangedFile = call.RelativePath,
            Diff = UnifiedDiff.Create(call.RelativePath, before, apply.NewContent!),
        };
    }
}
