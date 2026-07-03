namespace DevAgent.Worker.DotNet;

/// <summary>
/// Strongly-typed view of a .NET-upgrade job, sourced ENTIRELY from environment
/// variables (the container receives no host paths and no secrets beyond a
/// limited bot token). Shares the common worker environment variables with
/// <see cref="WorkerJobSettings"/> and adds the target framework.
///
/// SECURITY: Fails safely (throws) when a required variable is missing.
/// </summary>
public sealed record DotNetUpgradeWorkerSettings
{
    public required string JobId { get; init; }
    public required string CloneUrl { get; init; }
    public required string BaseBranch { get; init; }
    public required string TargetFramework { get; init; }
    public required string WorkspaceRoot { get; init; }
    public required string GitToken { get; init; }

    public bool OnlyUpgrade { get; init; } = true;

    /// <summary>Optional LLM provider enabling the in-sandbox build-repair step.</summary>
    public string? LlmProvider { get; init; }

    /// <summary>Optional model id for the build-repair agent.</summary>
    public string? LlmModel { get; init; }

    public static DotNetUpgradeWorkerSettings FromEnvironment(Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;

        var missing = new List<string>();

        string Require(string name)
        {
            var value = read(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                missing.Add(name);
                return string.Empty;
            }

            return value;
        }

        var jobId = Require(WorkerJobSettings.JobIdVar);
        var cloneUrl = Require(WorkerJobSettings.CloneUrlVar);
        var baseBranch = Require(WorkerJobSettings.BaseBranchVar);
        var targetFramework = Require(WorkerJobSettings.TargetFrameworkVar);
        var workspace = Require(WorkerJobSettings.WorkspaceRootVar);
        var gitToken = Require(WorkerJobSettings.GitTokenVar);

        if (missing.Count > 0)
        {
            throw new MissingWorkerConfigurationException(missing);
        }

        var onlyUpgradeRaw = read(WorkerJobSettings.OnlyUpgradeVar);
        var onlyUpgrade = onlyUpgradeRaw is null
            || !bool.TryParse(onlyUpgradeRaw, out var parsed) // default true if unset/garbage
            || parsed;

        return new DotNetUpgradeWorkerSettings
        {
            JobId = jobId,
            CloneUrl = cloneUrl,
            BaseBranch = baseBranch,
            TargetFramework = targetFramework,
            WorkspaceRoot = workspace,
            GitToken = gitToken,
            OnlyUpgrade = onlyUpgrade,
            LlmProvider = read(WorkerJobSettings.LlmProviderVar),
            LlmModel = read(WorkerJobSettings.LlmModelVar),
        };
    }
}
