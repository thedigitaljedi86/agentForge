namespace DevAgent.Contracts.Jobs;

/// <summary>
/// The kinds of jobs the platform knows how to run.
///
/// SECURITY: This enum is an allowlist of supported job types. The Runner
/// validates every incoming request against <see cref="JobType"/> policies,
/// so adding a value here is a deliberate, reviewable decision — not something
/// a caller can supply as an arbitrary string.
/// </summary>
public enum AgentJobType
{
    /// <summary>Unknown / unset. Always rejected by validation.</summary>
    Unknown = 0,

    /// <summary>Deterministic NuGet PackageReference update inside a sandbox worker.</summary>
    NuGetUpdate = 1,

    /// <summary>Future: LLM-assisted build/test fix inside a sandbox worker.</summary>
    LlmAssistedFix = 2,
}
