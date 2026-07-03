namespace DevAgent.Forge;

/// <summary>A unit of work handed to the coding agent.</summary>
public sealed record CodingAgentTask
{
    public required string JobId { get; init; }

    /// <summary>What the agent should achieve (e.g. "fix the build").</summary>
    public required string Goal { get; init; }

    /// <summary>
    /// Absolute path to the repository workspace the agent is confined to.
    /// All tool paths are validated relative to this root.
    /// </summary>
    public required string WorkspaceRoot { get; init; }

    /// <summary>Build/test errors to fix, if this is a repair task.</summary>
    public string? FailureContext { get; init; }

    /// <summary>
    /// Instructions from admin-registered SKILLS applied to this task. Skills
    /// add guidance, never capability — every tool they mention still has to
    /// pass the same policy gates as any other call.
    /// </summary>
    public string? SkillInstructions { get; init; }
}

/// <summary>One step in the agent loop: the tool that ran and its result.</summary>
public sealed record AgentStep
{
    public required ToolCallRequest Request { get; init; }
    public required ToolCallResult Result { get; init; }

    /// <summary>The model's short reasoning for taking this step (logged).</summary>
    public string? Reasoning { get; init; }
}

/// <summary>Outcome of a coding agent run.</summary>
public sealed record CodingAgentResult
{
    public required string JobId { get; init; }
    public required bool Succeeded { get; init; }

    /// <summary>The model's final summary of what it changed and why.</summary>
    public string? ReasoningSummary { get; init; }

    /// <summary>Aggregated unified diff of every applied change (saved + audited).</summary>
    public string FinalDiff { get; init; } = string.Empty;

    public IReadOnlyList<string> ChangedFiles { get; init; } = Array.Empty<string>();

    /// <summary>Number of agent iterations consumed.</summary>
    public int IterationsUsed { get; init; }

    /// <summary>True when the loop stopped because it hit the iteration cap.</summary>
    public bool StoppedAtIterationLimit { get; init; }

    /// <summary>Full ordered log of every tool call made during the run.</summary>
    public IReadOnlyList<AgentStep> Steps { get; init; } = Array.Empty<AgentStep>();
}

/// <summary>
/// What the LLM decided to do next: either request a tool call, or declare the
/// task complete with a summary. The model can never return a free-form command.
/// </summary>
public sealed record LlmDecision
{
    /// <summary>The next tool to run, or null when the model is done.</summary>
    public ToolCallRequest? ToolCall { get; init; }

    /// <summary>True when the model considers the task complete.</summary>
    public bool IsComplete { get; init; }

    /// <summary>Short reasoning for this step (audited).</summary>
    public string? Reasoning { get; init; }

    /// <summary>Final summary, set when <see cref="IsComplete"/> is true.</summary>
    public string? Summary { get; init; }
}

/// <summary>Knobs for the coding agent. Defaults are conservative.</summary>
public sealed class CodingAgentOptions
{
    /// <summary>Hard cap on agent iterations. SECURITY: bounds blast radius + cost.</summary>
    public int MaxIterations { get; set; } = 10;

    /// <summary>
    /// Whether the agent may edit deployment files (Dockerfile, k8s, terraform,
    /// CI workflows). Secrets are NEVER editable regardless of this flag.
    /// </summary>
    public bool AllowDeploymentFileEdits { get; set; } = false;

    /// <summary>
    /// WHERE the agent may write: AllowAll (repair agents), docs-only
    /// prefixes (DocScribe) or ReadOnly (CodeReviewer). Enforced by the
    /// file/patch tools, not by the prompt.
    /// </summary>
    public DevAgent.Guard.Policies.WriteScopePolicy WriteScope { get; set; }
        = DevAgent.Guard.Policies.WriteScopePolicy.AllowAll;
}

/// <summary>
/// The coding agent loop. It asks the LLM for the next tool call, validates and
/// runs it through structured tools (never a shell), feeds the result back, and
/// repeats until the model is done or the iteration cap is reached.
/// </summary>
public interface ICodingAgent
{
    Task<CodingAgentResult> RunAsync(CodingAgentTask task, CancellationToken cancellationToken = default);
}

/// <summary>Thin abstraction over an LLM tool-calling client.</summary>
public interface ILlmClient
{
    Task<LlmDecision> GetNextDecisionAsync(
        CodingAgentTask task,
        IReadOnlyList<AgentStep> history,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Validates and dispatches a single tool call. Implementations MUST enforce
/// the tool allowlist, workspace path validation and protected-file rules
/// before executing anything, and MUST audit-log every call.
/// </summary>
public interface IToolCallHandler
{
    Task<ToolCallResult> HandleAsync(ToolCallRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Executes a single MCP tool call. Implementations MUST validate the call
/// against the server registry and the agent's grants (fail closed) before
/// contacting the gateway, and must never receive server credentials.
/// The composition root (the worker) supplies this; when absent, every
/// McpToolCall is denied.
/// </summary>
public interface IMcpToolExecutor
{
    Task<ToolCallResult> ExecuteAsync(McpToolCall call, CancellationToken cancellationToken = default);
}
