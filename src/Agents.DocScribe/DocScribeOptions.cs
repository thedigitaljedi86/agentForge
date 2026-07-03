namespace Agents.DocScribe;

using DevAgent.Bridge.Llm;

/// <summary>
/// Configuration for the DocScribe agent: which repositories (by allowlist
/// key) it keeps documented, and its LLM pin for the optional authoring step.
/// Without an LLM, DocScribe still maintains the deterministic code map.
/// </summary>
public sealed class DocScribeOptions
{
    public const string SectionName = "DocScribe";

    /// <summary>Allowlist keys of repositories DocScribe documents.</summary>
    public List<string> RepositoryKeys { get; set; } = new();

    /// <summary>LLM pin for the docs-scoped authoring agent (optional).</summary>
    public LlmClientOptions Llm { get; set; } = new();
}
