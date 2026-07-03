namespace DevAgent.Forge.Tools;

using DevAgent.Audit;

/// <summary>
/// Validates and dispatches a single structured tool call to the right tool,
/// auditing every call.
///
/// SECURITY: Two independent gates run before any tool executes:
///   1. The tool NAME must pass <see cref="ToolPolicy"/> (closed allowlist).
///   2. The tool's own path/protected-file checks run inside each tool.
/// Every call — allowed, denied or failed — is written to the audit log, and
/// mutating calls additionally emit a diff audit event.
/// </summary>
public sealed class CodingAgentToolHandler : IToolCallHandler
{
    private readonly ToolPolicy _toolPolicy;
    private readonly WorkspaceFileTool _files;
    private readonly PatchApplicationService _patches;
    private readonly DotNetCommandTools _commands;
    private readonly IAuditLog _audit;
    private readonly string _jobId;
    private readonly IMcpToolExecutor? _mcp;

    public CodingAgentToolHandler(
        ToolPolicy toolPolicy,
        WorkspaceFileTool files,
        PatchApplicationService patches,
        DotNetCommandTools commands,
        IAuditLog audit,
        string jobId,
        IMcpToolExecutor? mcp = null)
    {
        _mcp = mcp;
        _toolPolicy = toolPolicy;
        _files = files;
        _patches = patches;
        _commands = commands;
        _audit = audit;
        _jobId = jobId;
    }

    public async Task<ToolCallResult> HandleAsync(ToolCallRequest request, CancellationToken cancellationToken = default)
    {
        // MCP calls have their own gate (registry ∩ agent grants, enforced by
        // the executor, which fails closed when absent).
        if (request is McpToolCall mcpCall)
        {
            var mcpResult = _mcp is null
                ? ToolCallResult.Denied(mcpCall, "No MCP access is configured for this job.")
                : await _mcp.ExecuteAsync(mcpCall, cancellationToken);
            await AuditAsync(request, mcpResult, cancellationToken);
            return mcpResult;
        }

        // Gate 1: tool-name allowlist (defence in depth on top of typed calls).
        var nameCheck = _toolPolicy.Validate(request.ToolName);
        if (!nameCheck.IsValid)
        {
            var denied = ToolCallResult.Denied(request, nameCheck.Reason!);
            await AuditAsync(request, denied, cancellationToken);
            return denied;
        }

        var result = request switch
        {
            ListFilesToolCall c => await _files.ListAsync(c, cancellationToken),
            ReadFileToolCall c => await _files.ReadAsync(c, cancellationToken),
            ReplaceFileToolCall c => await _files.ReplaceAsync(c, cancellationToken),
            ApplyPatchToolCall c => await _patches.ApplyAsync(c, cancellationToken),
            RunBuildToolCall c => await _commands.BuildAsync(c, cancellationToken),
            RunTestToolCall c => await _commands.TestAsync(c, cancellationToken),
            GitStatusToolCall c => await _commands.GitStatusAsync(c, cancellationToken),

            // Unknown concrete type — refuse rather than guess.
            _ => ToolCallResult.Denied(request, $"No handler for tool '{request.ToolName}'."),
        };

        await AuditAsync(request, result, cancellationToken);
        return result;
    }

    private async Task AuditAsync(ToolCallRequest request, ToolCallResult result, CancellationToken ct)
    {
        await _audit.WriteAsync(new ToolCallAuditEvent
        {
            JobId = _jobId,
            Actor = "CodingAgent",
            ToolName = request.ToolName,
            Arguments = Describe(request),
            Allowed = !result.DeniedByPolicy,
            DenyReason = result.DeniedByPolicy ? result.Error : null,
        }, ct);

        if (result.Diff is { Length: > 0 } diff && result.ChangedFile is { } file)
        {
            await _audit.WriteAsync(new DiffAuditEvent
            {
                JobId = _jobId,
                Actor = "CodingAgent",
                FilePath = file,
                UnifiedDiff = diff,
            }, ct);
        }
    }

    // A compact, non-sensitive description for the audit trail. We deliberately
    // do NOT log full file contents here (diffs are logged separately).
    private static string Describe(ToolCallRequest request) => request switch
    {
        ListFilesToolCall c => $"path='{c.RelativePath}' recursive={c.Recursive}",
        ReadFileToolCall c => $"path='{c.RelativePath}'",
        ReplaceFileToolCall c => $"path='{c.RelativePath}' bytes={c.NewContent.Length}",
        ApplyPatchToolCall c => $"path='{c.RelativePath}' patchBytes={c.UnifiedDiff.Length}",
        RunBuildToolCall c => $"project='{c.ProjectOrSolution}'",
        RunTestToolCall c => $"project='{c.ProjectOrSolution}'",
        GitStatusToolCall => "(no args)",
        McpToolCall c => $"server='{c.ServerKey}' tool='{c.Tool}' argBytes={c.ArgumentsJson.Length}",
        _ => "(unknown)",
    };
}
