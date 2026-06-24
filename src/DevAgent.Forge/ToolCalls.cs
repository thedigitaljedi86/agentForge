namespace DevAgent.Forge;

/// <summary>
/// Base type for a structured tool call requested by an LLM.
///
/// SECURITY: This is a CLOSED set of tools. There is intentionally NO tool for
/// bash, powershell, curl, wget, ssh, docker, kubectl, az, aws or any generic
/// command execution. The LLM can only ever produce one of the concrete types
/// below, every one of which is path- and policy-validated before it runs.
/// Adding a tool is a deliberate, reviewable code change.
/// </summary>
public abstract record ToolCallRequest
{
    public string ToolCallId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Stable wire name used by the LLM tool schema + audit logs.</summary>
    public abstract string ToolName { get; }
}

/// <summary>List files/directories under a workspace-relative directory.</summary>
public sealed record ListFilesToolCall : ToolCallRequest
{
    public override string ToolName => "list_files";

    /// <summary>Workspace-relative directory. "" means the workspace root.</summary>
    public string RelativePath { get; init; } = string.Empty;

    /// <summary>Recurse into sub-directories.</summary>
    public bool Recursive { get; init; } = false;
}

/// <summary>Read a file from the workspace (path is workspace-relative).</summary>
public sealed record ReadFileToolCall : ToolCallRequest
{
    public override string ToolName => "read_file";
    public required string RelativePath { get; init; }
}

/// <summary>
/// Apply a unified-diff patch to a workspace file. Subject to workspace path
/// validation and <see cref="DevAgent.Guard.Policies.ProtectedFilePolicy"/>.
/// </summary>
public sealed record ApplyPatchToolCall : ToolCallRequest
{
    public override string ToolName => "apply_patch";
    public required string RelativePath { get; init; }
    public required string UnifiedDiff { get; init; }
}

/// <summary>
/// Replace a workspace file's entire contents. Same validation as apply_patch.
/// </summary>
public sealed record ReplaceFileToolCall : ToolCallRequest
{
    public override string ToolName => "replace_file";
    public required string RelativePath { get; init; }
    public required string NewContent { get; init; }
}

/// <summary>Run "dotnet build" via the SafeCommandRunner.</summary>
public sealed record RunBuildToolCall : ToolCallRequest
{
    public override string ToolName => "run_dotnet_build";

    /// <summary>Workspace-relative project/solution dir. "" means workspace root.</summary>
    public string ProjectOrSolution { get; init; } = string.Empty;
}

/// <summary>Run "dotnet test" via the SafeCommandRunner.</summary>
public sealed record RunTestToolCall : ToolCallRequest
{
    public override string ToolName => "run_dotnet_test";
    public string ProjectOrSolution { get; init; } = string.Empty;
}

/// <summary>Run "git status" via the SafeCommandRunner (read-only).</summary>
public sealed record GitStatusToolCall : ToolCallRequest
{
    public override string ToolName => "git_status";
}

/// <summary>Result of a single tool call.</summary>
public sealed record ToolCallResult
{
    public required string ToolCallId { get; init; }
    public required string ToolName { get; init; }
    public required bool Success { get; init; }
    public string? Output { get; init; }
    public string? Error { get; init; }

    /// <summary>True when the tool was DENIED by policy (vs. failed to run).</summary>
    public bool DeniedByPolicy { get; init; }

    /// <summary>For mutating tools: the file that changed (workspace-relative).</summary>
    public string? ChangedFile { get; init; }

    /// <summary>For mutating tools: the unified diff that was applied.</summary>
    public string? Diff { get; init; }

    public static ToolCallResult Denied(ToolCallRequest request, string reason) => new()
    {
        ToolCallId = request.ToolCallId,
        ToolName = request.ToolName,
        Success = false,
        DeniedByPolicy = true,
        Error = reason,
    };

    public static ToolCallResult Ok(ToolCallRequest request, string? output = null) => new()
    {
        ToolCallId = request.ToolCallId,
        ToolName = request.ToolName,
        Success = true,
        Output = output,
    };

    public static ToolCallResult Fail(ToolCallRequest request, string error) => new()
    {
        ToolCallId = request.ToolCallId,
        ToolName = request.ToolName,
        Success = false,
        Error = error,
    };
}
