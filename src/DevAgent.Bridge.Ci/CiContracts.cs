namespace DevAgent.Bridge.Ci;

/// <summary>Supported CI systems.</summary>
public enum CiProviderKind
{
    GitHubActions = 1,
    GitLabCi = 2,
    AzureDevOpsPipelines = 3,
}

/// <summary>
/// An administrator-configured CI connection for one allowlisted repository.
///
/// SECURITY: <see cref="TokenEnvVar"/> is a REFERENCE — the name of an
/// environment variable on the Hub host holding the (read-only) CI token.
/// The value is never stored, never displayed and never enters a sandbox.
/// </summary>
public sealed record CiConnection
{
    public required string RepositoryKey { get; init; }
    public required CiProviderKind Provider { get; init; }

    /// <summary>API base URL (e.g. https://api.github.com, https://gitlab.example.com, https://dev.azure.com).</summary>
    public required string BaseUrl { get; init; }

    /// <summary>
    /// Provider-specific project path: "owner/repo" (GitHub),
    /// "group/project" (GitLab), "organization/project" (Azure DevOps).
    /// </summary>
    public required string ProjectPath { get; init; }

    /// <summary>Env-var NAME on the Hub host holding the CI token.</summary>
    public string? TokenEnvVar { get; init; }
}

/// <summary>One failed pipeline/build run, provider-neutral.</summary>
public sealed record CiPipelineRun
{
    public required string RunId { get; init; }
    public required string Branch { get; init; }
    public string Title { get; init; } = string.Empty;
    public string WebUrl { get; init; } = string.Empty;
    public DateTimeOffset? FinishedUtc { get; init; }
}

/// <summary>
/// Read-only CI operations. Deliberately NO trigger/cancel/retry surface —
/// PipelineDoctor diagnoses and proposes a PR; it never operates the CI system.
/// </summary>
public interface ICiProvider
{
    /// <summary>Most recent failed runs on the connection's repository.</summary>
    Task<IReadOnlyList<CiPipelineRun>> ListFailedRunsAsync(
        CiConnection connection, int top = 10, CancellationToken ct = default);

    /// <summary>
    /// The failure log for one run, flattened to text and truncated to the
    /// TAIL (the end is where errors live) so it stays prompt-sized.
    /// </summary>
    Task<string> GetFailureLogAsync(
        CiConnection connection, string runId, CancellationToken ct = default);
}

/// <summary>Seam the Hub implements store-backed: CI connection per repository.</summary>
public interface ICiConnectionSource
{
    Task<CiConnection?> GetAsync(string repositoryKey, CancellationToken ct = default);
}

/// <summary>Seam for deduplicating already-handled failed runs.</summary>
public interface IProcessedRunStore
{
    Task<bool> IsProcessedAsync(string repositoryKey, string runId, CancellationToken ct = default);
    Task MarkProcessedAsync(string repositoryKey, string runId, CancellationToken ct = default);
}

/// <summary>Shared helpers for the concrete providers.</summary>
internal static class CiLog
{
    public const int MaxLogChars = 12_000;

    /// <summary>Keeps the TAIL of a log (errors are at the end).</summary>
    public static string TruncateTail(string log) =>
        log.Length <= MaxLogChars ? log : "…(log truncated)\n" + log[^MaxLogChars..];
}
