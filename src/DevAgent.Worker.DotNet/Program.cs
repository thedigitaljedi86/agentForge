using DevAgent.Bridge.Git;
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
//  * The worker does not talk to Docker and does not access host paths.

try
{
    var settings = WorkerJobSettings.FromEnvironment();

    var pathValidator = new WorkspacePathValidator(settings.WorkspaceRoot);
    var commandRunner = new SafeCommandRunner(new CommandPolicy(), pathValidator);
    var updater = new PackageReferenceUpdater(settings.OnlyUpgrade);

    // First milestone uses the placeholder provider. A real provider is wired
    // in later without changing the worker, thanks to the IGitProvider seam.
    IGitProvider gitProvider = new PlaceholderGitProvider();

    var worker = new NuGetUpdateWorker(commandRunner, pathValidator, updater, gitProvider);

    var result = await worker.RunAsync(settings);

    Console.WriteLine($"[worker] job={result.JobId} status={result.Status} pr={result.PullRequestUrl} :: {result.Message}");
    return result.Status is DevAgent.Contracts.Jobs.AgentJobStatus.Failed ? 1 : 0;
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
