namespace DevAgent.Forge;

/// <summary>A unit of work handed to a coding agent (future LLM use).</summary>
public sealed record CodingAgentTask
{
    public required string JobId { get; init; }
    public required string Goal { get; init; }

    /// <summary>Workspace-relative root the agent is confined to.</summary>
    public required string WorkspaceRoot { get; init; }

    /// <summary>Build/test errors to fix, if this is a repair task.</summary>
    public string? FailureContext { get; init; }
}

/// <summary>Outcome of a coding agent run.</summary>
public sealed record CodingAgentResult
{
    public required string JobId { get; init; }
    public required bool Succeeded { get; init; }
    public string? Summary { get; init; }
    public IReadOnlyList<string> ChangedFiles { get; init; } = Array.Empty<string>();
}

/// <summary>
/// The coding agent loop abstraction. The implementation will: ask the LLM for
/// the next tool call, validate it through Guard, execute it via structured
/// tools, feed results back, and repeat — never touching a shell directly.
/// </summary>
public interface ICodingAgent
{
    Task<CodingAgentResult> RunAsync(CodingAgentTask task, CancellationToken cancellationToken = default);
}

/// <summary>Thin abstraction over an LLM completion/tool-calling client.</summary>
public interface ILlmClient
{
    /// <summary>
    /// Given the conversation so far, returns the next tool call the model
    /// wants to make, or null when the model considers the task complete.
    /// </summary>
    Task<ToolCallRequest?> GetNextToolCallAsync(
        CodingAgentTask task,
        IReadOnlyList<ToolCallResult> history,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Dispatches a validated tool call to the right tool. Implementations MUST
/// run every call past DevAgent.Guard before executing it.
/// </summary>
public interface IToolCallHandler
{
    Task<ToolCallResult> HandleAsync(ToolCallRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Reads files within the workspace (policy-controlled).</summary>
public interface IFileTool
{
    Task<ToolCallResult> ReadAsync(ReadFileToolCall call, CancellationToken cancellationToken = default);
}

/// <summary>Applies patches within the workspace (policy-controlled).</summary>
public interface IPatchTool
{
    Task<ToolCallResult> ApplyAsync(ApplyPatchToolCall call, CancellationToken cancellationToken = default);
}

/// <summary>Runs the build and returns structured feedback.</summary>
public interface IBuildTool
{
    Task<ToolCallResult> BuildAsync(RunBuildToolCall call, CancellationToken cancellationToken = default);
}

/// <summary>Runs the tests and returns structured feedback.</summary>
public interface ITestTool
{
    Task<ToolCallResult> TestAsync(RunTestToolCall call, CancellationToken cancellationToken = default);
}
