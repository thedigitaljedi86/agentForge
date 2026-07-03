namespace DevAgent.Forge.Tests.Fakes;

using DevAgent.Forge;

/// <summary>
/// A deterministic fake <see cref="ILlmClient"/> for tests. It returns a
/// pre-scripted sequence of decisions, ignoring the actual model. This lets us
/// exercise the agent loop and the policy gates without any real LLM.
/// </summary>
public sealed class ScriptedLlmClient : ILlmClient
{
    private readonly Queue<LlmDecision> _script;

    /// <summary>Records the history the agent passed on each call (for assertions).</summary>
    public List<int> ObservedHistoryLengths { get; } = new();

    public ScriptedLlmClient(IEnumerable<LlmDecision> script)
    {
        _script = new Queue<LlmDecision>(script);
    }

    public Task<LlmDecision> GetNextDecisionAsync(
        CodingAgentTask task,
        IReadOnlyList<AgentStep> history,
        CancellationToken cancellationToken = default)
    {
        ObservedHistoryLengths.Add(history.Count);

        // When the script is exhausted, keep asking for the same harmless tool
        // (git_status) so we can test the iteration cap with a model that never
        // declares completion.
        if (_script.Count == 0)
        {
            return Task.FromResult(new LlmDecision
            {
                ToolCall = new GitStatusToolCall(),
                Reasoning = "still working",
            });
        }

        return Task.FromResult(_script.Dequeue());
    }
}
