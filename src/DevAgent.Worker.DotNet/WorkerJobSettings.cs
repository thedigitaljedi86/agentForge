namespace DevAgent.Worker.DotNet;

/// <summary>
/// Strongly-typed view of the job the worker must perform, sourced ENTIRELY
/// from environment variables. The container receives no secrets beyond a
/// limited bot token, and no host paths.
///
/// SECURITY: The worker fails safely (throws) if any required variable is
/// missing, rather than guessing defaults that could point at the wrong repo.
/// </summary>
public sealed record WorkerJobSettings
{
    public required string JobId { get; init; }
    public required string CloneUrl { get; init; }
    public required string BaseBranch { get; init; }
    public required string PackageId { get; init; }
    public required string TargetVersion { get; init; }

    /// <summary>Workspace root inside the container (e.g. /workspace).</summary>
    public required string WorkspaceRoot { get; init; }

    /// <summary>Limited bot/service-account token for git push + PR creation.</summary>
    public required string GitToken { get; init; }

    public bool OnlyUpgrade { get; init; } = true;

    /// <summary>
    /// Optional LLM provider (Claude/OpenAi/Gemini) enabling the in-sandbox
    /// build-repair step. When null/unparseable, build repair is disabled.
    /// </summary>
    public string? LlmProvider { get; init; }

    /// <summary>Optional model id for the build-repair agent (provider default when null).</summary>
    public string? LlmModel { get; init; }

    // Environment variable names, namespaced to avoid collisions.
    public const string JobTypeVar = "DEVAGENT_JOB_TYPE";
    public const string JobIdVar = "DEVAGENT_JOB_ID";
    public const string CloneUrlVar = "DEVAGENT_CLONE_URL";
    public const string BaseBranchVar = "DEVAGENT_BASE_BRANCH";
    public const string PackageIdVar = "DEVAGENT_PACKAGE_ID";
    public const string TargetVersionVar = "DEVAGENT_TARGET_VERSION";
    public const string TargetFrameworkVar = "DEVAGENT_TARGET_FRAMEWORK";
    public const string WorkspaceRootVar = "DEVAGENT_WORKSPACE";
    public const string GitTokenVar = "DEVAGENT_GIT_TOKEN";
    public const string OnlyUpgradeVar = "DEVAGENT_ONLY_UPGRADE";
    public const string LlmProviderVar = "DEVAGENT_LLM_PROVIDER";
    public const string LlmModelVar = "DEVAGENT_LLM_MODEL";
    public const string FailureContextVar = "DEVAGENT_FAILURE_CONTEXT";
    public const string SourceBranchVar = "DEVAGENT_SOURCE_BRANCH";
    public const string PrNumberVar = "DEVAGENT_PR_NUMBER";

    /// <summary>
    /// Builds settings from a variable lookup (defaults to the process
    /// environment). Throws <see cref="MissingWorkerConfigurationException"/>
    /// listing every missing required variable so the failure is actionable.
    /// </summary>
    public static WorkerJobSettings FromEnvironment(Func<string, string?>? read = null)
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

        var jobId = Require(JobIdVar);
        var cloneUrl = Require(CloneUrlVar);
        var baseBranch = Require(BaseBranchVar);
        var packageId = Require(PackageIdVar);
        var targetVersion = Require(TargetVersionVar);
        var workspace = Require(WorkspaceRootVar);
        var gitToken = Require(GitTokenVar);

        if (missing.Count > 0)
        {
            throw new MissingWorkerConfigurationException(missing);
        }

        var onlyUpgradeRaw = read(OnlyUpgradeVar);
        var onlyUpgrade = onlyUpgradeRaw is null
            || !bool.TryParse(onlyUpgradeRaw, out var parsed) // default true if unset/garbage
            || parsed;

        return new WorkerJobSettings
        {
            JobId = jobId,
            CloneUrl = cloneUrl,
            BaseBranch = baseBranch,
            PackageId = packageId,
            TargetVersion = targetVersion,
            WorkspaceRoot = workspace,
            GitToken = gitToken,
            OnlyUpgrade = onlyUpgrade,
            LlmProvider = read(LlmProviderVar),
            LlmModel = read(LlmModelVar),
        };
    }
}

/// <summary>Thrown when the worker is started without its required configuration.</summary>
public sealed class MissingWorkerConfigurationException : Exception
{
    public MissingWorkerConfigurationException(IReadOnlyCollection<string> missingVariables)
        : base($"Missing required environment variables: {string.Join(", ", missingVariables)}")
    {
        MissingVariables = missingVariables;
    }

    public IReadOnlyCollection<string> MissingVariables { get; }
}
