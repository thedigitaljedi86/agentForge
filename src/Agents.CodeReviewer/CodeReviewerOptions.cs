namespace Agents.CodeReviewer;

using DevAgent.Bridge.Llm;

/// <summary>
/// Configuration for the CodeReviewer agent: which repositories (by allowlist
/// key) it reviews PRs for, and its LLM pin (a review needs one).
/// </summary>
public sealed class CodeReviewerOptions
{
    public const string SectionName = "CodeReviewer";

    /// <summary>Allowlist keys of repositories CodeReviewer reviews.</summary>
    public List<string> RepositoryKeys { get; set; } = new();

    /// <summary>LLM pin for the read-only review agent.</summary>
    public LlmClientOptions Llm { get; set; } = new();
}
