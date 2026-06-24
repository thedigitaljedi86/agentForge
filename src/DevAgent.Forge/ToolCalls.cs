namespace DevAgent.Forge;

/// <summary>
/// Base type for a structured tool call requested by an LLM. There is NO
/// "run arbitrary command" or "shell" tool — only the explicit, named tool
/// calls below. Adding a new tool is a deliberate, reviewable change.
/// </summary>
public abstract record ToolCallRequest
{
    public string ToolCallId { get; init; } = Guid.NewGuid().ToString("N");
    public abstract string ToolName { get; }
}

/// <summary>Read a file from the workspace (path is workspace-relative).</summary>
public sealed record ReadFileToolCall : ToolCallRequest
{
    public override string ToolName => "read_file";
    public required string RelativePath { get; init; }
}

/// <summary>
/// Apply a unified-diff patch to a workspace file. Subject to
/// <see cref="DevAgent.Guard.Policies.ProtectedFilePolicy"/> and workspace
/// path validation before it is allowed to run.
/// </summary>
public sealed record ApplyPatchToolCall : ToolCallRequest
{
    public override string ToolName => "apply_patch";
    public required string RelativePath { get; init; }
    public required string UnifiedDiff { get; init; }
}

/// <summary>Run the build via the SafeCommandRunner (dotnet build).</summary>
public sealed record RunBuildToolCall : ToolCallRequest
{
    public override string ToolName => "run_build";
    public string? ProjectOrSolution { get; init; }
}

/// <summary>Run the tests via the SafeCommandRunner (dotnet test).</summary>
public sealed record RunTestToolCall : ToolCallRequest
{
    public override string ToolName => "run_test";
    public string? ProjectOrSolution { get; init; }
}

/// <summary>Result of a single tool call.</summary>
public sealed record ToolCallResult
{
    public required string ToolCallId { get; init; }
    public required bool Success { get; init; }
    public string? Output { get; init; }
    public string? Error { get; init; }

    /// <summary>True when the tool was denied by policy (vs. failed to run).</summary>
    public bool DeniedByPolicy { get; init; }
}
