using DevAgent.Audit;
using DevAgent.Bridge.Git;
using DevAgent.Bridge.Llm;
using DevAgent.Forge;
using DevAgent.Guard.Execution;
using DevAgent.Guard.Paths;
using DevAgent.Guard.Policies;
using DevAgent.Worker.DotNet;

// Entry point for the sandbox worker container.
//
// SECURITY NOTES:
//  * All configuration arrives via environment variables; missing required
//    variables cause a safe failure (exit code 2) before any work begins.
//  * Commands are constrained to git + dotnet by CommandPolicy.
//  * All file access is confined to the workspace by WorkspacePathValidator.
//  * The worker does not talk to Podman/Docker and does not access host paths.
//  * The OPTIONAL build-repair agent (Forge) acts only through structured,
//    policy-checked tools — never a shell — and never merges anything.

try
{
    var read = (Func<string, string?>)Environment.GetEnvironmentVariable;
    var jobType = read(WorkerJobSettings.JobTypeVar) ?? "NuGetUpdate";

    // First milestone uses the placeholder git provider. A real provider is
    // wired in later without changing the worker, thanks to the IGitProvider seam.
    IGitProvider gitProvider = new PlaceholderGitProvider();
    IAuditLog audit = new ConsoleAuditLog();
    using var http = new HttpClient();

    int exitCode;
    if (string.Equals(jobType, "DotNetUpgrade", StringComparison.OrdinalIgnoreCase))
    {
        var settings = DotNetUpgradeWorkerSettings.FromEnvironment(read);
        var (commandRunner, pathValidator) = BuildGuards(settings.WorkspaceRoot);
        var repair = BuildRepairFactory(settings.LlmProvider, settings.LlmModel, settings.JobId, audit, http);

        var worker = new DotNetUpgradeWorker(commandRunner, pathValidator, new TargetFrameworkUpdater(settings.OnlyUpgrade), gitProvider, repair);
        var result = await worker.RunAsync(settings);
        Console.WriteLine($"[worker] job={result.JobId} type=DotNetUpgrade status={result.Status} pr={result.PullRequestUrl} :: {result.Message}");
        exitCode = result.Status is DevAgent.Contracts.Jobs.AgentJobStatus.Failed ? 1 : 0;
    }
    else
    {
        var settings = WorkerJobSettings.FromEnvironment(read);
        var (commandRunner, pathValidator) = BuildGuards(settings.WorkspaceRoot);
        var repair = BuildRepairFactory(settings.LlmProvider, settings.LlmModel, settings.JobId, audit, http);

        var worker = new NuGetUpdateWorker(commandRunner, pathValidator, new PackageReferenceUpdater(settings.OnlyUpgrade), gitProvider, repair);
        var result = await worker.RunAsync(settings);
        Console.WriteLine($"[worker] job={result.JobId} type=NuGetUpdate status={result.Status} pr={result.PullRequestUrl} :: {result.Message}");
        exitCode = result.Status is DevAgent.Contracts.Jobs.AgentJobStatus.Failed ? 1 : 0;
    }

    return exitCode;
}
catch (MissingWorkerConfigurationException ex)
{
    // Fail safely and loudly when the container was started without its config.
    Console.Error.WriteLine($"[worker] configuration error: {ex.Message}");
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[worker] unexpected error: {ex.Message}");
    return 3;
}

static (SafeCommandRunner, WorkspacePathValidator) BuildGuards(string workspaceRoot)
{
    var pathValidator = new WorkspacePathValidator(workspaceRoot);
    var commandRunner = new SafeCommandRunner(new CommandPolicy(), pathValidator);
    return (commandRunner, pathValidator);
}

// Build the opt-in Forge build-repair agent factory. Returns null (repair
// disabled) when no/unknown LLM provider is configured. The agent is rooted at
// the cloned repo path supplied by the workflow, so it is confined to the repo.
static Func<string, ICodingAgent>? BuildRepairFactory(string? providerName, string? model, string jobId, IAuditLog audit, HttpClient http)
{
    if (!Enum.TryParse<LlmProvider>(providerName, ignoreCase: true, out var provider))
    {
        return null;
    }

    var options = new LlmClientOptions { Provider = provider, Model = model };
    var llm = LlmClientFactory.Create(options, http);
    return repoPath => CodingAgentFactory.Create(repoPath, llm, audit, jobId);
}
