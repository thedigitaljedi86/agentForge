namespace DevAgent.Worker.DotNet;

/// <summary>
/// Settings for a PipelineFix job, sourced entirely from environment variables
/// (same rules as every other worker: no host paths, no secrets beyond the
/// limited bot token; missing required variables fail safely before any work).
///
/// The CI failure log arrives as CONTEXT for the repair agent — it never
/// carries authority; the agent stays inside the same tool/policy cage.
/// </summary>
public sealed record PipelineFixWorkerSettings
{
    public required string JobId { get; init; }
    public required string CloneUrl { get; init; }

    /// <summary>The FAILING branch — the repair starts from it.</summary>
    public required string BaseBranch { get; init; }

    public required string WorkspaceRoot { get; init; }
    public required string GitToken { get; init; }

    /// <summary>CI failure log tail (optional; may be empty).</summary>
    public string FailureContext { get; init; } = string.Empty;

    public string? LlmProvider { get; init; }
    public string? LlmModel { get; init; }

    public static PipelineFixWorkerSettings FromEnvironment(Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        var required = new RequiredEnvironment(read);

        var jobId = required.Require(WorkerJobSettings.JobIdVar);
        var cloneUrl = required.Require(WorkerJobSettings.CloneUrlVar);
        var baseBranch = required.Require(WorkerJobSettings.BaseBranchVar);
        var workspace = required.Require(WorkerJobSettings.WorkspaceRootVar);
        var gitToken = required.Require(WorkerJobSettings.GitTokenVar);
        required.ThrowIfMissing();

        return new PipelineFixWorkerSettings
        {
            JobId = jobId,
            CloneUrl = cloneUrl,
            BaseBranch = baseBranch,
            WorkspaceRoot = workspace,
            GitToken = gitToken,
            FailureContext = read(WorkerJobSettings.FailureContextVar) ?? string.Empty,
            LlmProvider = read(WorkerJobSettings.LlmProviderVar),
            LlmModel = read(WorkerJobSettings.LlmModelVar),
        };
    }
}

/// <summary>Settings for a DocUpdate (DocScribe) job.</summary>
public sealed record DocUpdateWorkerSettings
{
    public required string JobId { get; init; }
    public required string CloneUrl { get; init; }
    public required string BaseBranch { get; init; }
    public required string WorkspaceRoot { get; init; }
    public required string GitToken { get; init; }

    public string? LlmProvider { get; init; }
    public string? LlmModel { get; init; }

    public static DocUpdateWorkerSettings FromEnvironment(Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        var required = new RequiredEnvironment(read);

        var jobId = required.Require(WorkerJobSettings.JobIdVar);
        var cloneUrl = required.Require(WorkerJobSettings.CloneUrlVar);
        var baseBranch = required.Require(WorkerJobSettings.BaseBranchVar);
        var workspace = required.Require(WorkerJobSettings.WorkspaceRootVar);
        var gitToken = required.Require(WorkerJobSettings.GitTokenVar);
        required.ThrowIfMissing();

        return new DocUpdateWorkerSettings
        {
            JobId = jobId,
            CloneUrl = cloneUrl,
            BaseBranch = baseBranch,
            WorkspaceRoot = workspace,
            GitToken = gitToken,
            LlmProvider = read(WorkerJobSettings.LlmProviderVar),
            LlmModel = read(WorkerJobSettings.LlmModelVar),
        };
    }
}

/// <summary>Settings for a CodeReview job (read-only; comment is the only output).</summary>
public sealed record CodeReviewWorkerSettings
{
    public required string JobId { get; init; }
    public required string CloneUrl { get; init; }

    /// <summary>The PR's TARGET branch (the review baseline).</summary>
    public required string BaseBranch { get; init; }

    /// <summary>The PR's source branch whose changes are reviewed.</summary>
    public required string SourceBranch { get; init; }

    public required string WorkspaceRoot { get; init; }
    public required string GitToken { get; init; }

    /// <summary>Provider PR number for posting the review comment (optional).</summary>
    public int? PrNumber { get; init; }

    public string? LlmProvider { get; init; }
    public string? LlmModel { get; init; }

    public static CodeReviewWorkerSettings FromEnvironment(Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        var required = new RequiredEnvironment(read);

        var jobId = required.Require(WorkerJobSettings.JobIdVar);
        var cloneUrl = required.Require(WorkerJobSettings.CloneUrlVar);
        var baseBranch = required.Require(WorkerJobSettings.BaseBranchVar);
        var sourceBranch = required.Require(WorkerJobSettings.SourceBranchVar);
        var workspace = required.Require(WorkerJobSettings.WorkspaceRootVar);
        var gitToken = required.Require(WorkerJobSettings.GitTokenVar);
        required.ThrowIfMissing();

        var prRaw = read(WorkerJobSettings.PrNumberVar);
        int? prNumber = int.TryParse(prRaw, out var parsed) && parsed > 0 ? parsed : null;

        return new CodeReviewWorkerSettings
        {
            JobId = jobId,
            CloneUrl = cloneUrl,
            BaseBranch = baseBranch,
            SourceBranch = sourceBranch,
            WorkspaceRoot = workspace,
            GitToken = gitToken,
            PrNumber = prNumber,
            LlmProvider = read(WorkerJobSettings.LlmProviderVar),
            LlmModel = read(WorkerJobSettings.LlmModelVar),
        };
    }
}

/// <summary>
/// Small helper shared by the settings records: collects the names of every
/// missing required variable so a misconfigured container fails with ONE
/// actionable message instead of dying on the first lookup.
/// </summary>
internal sealed class RequiredEnvironment
{
    private readonly Func<string, string?> _read;
    private readonly List<string> _missing = new();

    public RequiredEnvironment(Func<string, string?> read) => _read = read;

    public string Require(string name)
    {
        var value = _read(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            _missing.Add(name);
            return string.Empty;
        }

        return value;
    }

    public void ThrowIfMissing()
    {
        if (_missing.Count > 0)
        {
            throw new MissingWorkerConfigurationException(_missing);
        }
    }
}
