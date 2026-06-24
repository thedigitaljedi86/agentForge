namespace DevAgent.Bridge.Llm;

/// <summary>
/// Raised when an LLM provider call fails (non-success HTTP status, malformed
/// response, or missing credentials). The coding agent loop surfaces this as a
/// failed job rather than silently degrading — the result is always reviewable.
/// </summary>
public sealed class LlmClientException : Exception
{
    public LlmClientException(string message) : base(message) { }
    public LlmClientException(string message, Exception inner) : base(message, inner) { }
}
