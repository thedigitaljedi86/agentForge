namespace Agents.PipelineDoctor;

using DevAgent.Bridge.Llm;

/// <summary>
/// Configuration for the PipelineDoctor agent: which repositories (by
/// allowlist key) it watches for failing pipelines, and its LLM pin for the
/// repair step. The CI connection per repository (provider, base URL, project
/// path, token env-var NAME) is admin-managed in the store.
/// </summary>
public sealed class PipelineDoctorOptions
{
    public const string SectionName = "PipelineDoctor";

    /// <summary>Allowlist keys of repositories PipelineDoctor watches.</summary>
    public List<string> RepositoryKeys { get; set; } = new();

    /// <summary>Failed runs fetched per repository per sweep.</summary>
    public int RunsPerSweep { get; set; } = 5;

    /// <summary>LLM pin for the repair agent (a pipeline fix needs one).</summary>
    public LlmClientOptions Llm { get; set; } = new();
}
