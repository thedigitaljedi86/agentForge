using System.Text.Json;
using DevAgent.Audit;
using DevAgent.Bridge.Git;
using DevAgent.Bridge.Llm;
using DevAgent.Bridge.Mcp;
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
    if (string.Equals(jobType, "PipelineFix", StringComparison.OrdinalIgnoreCase))
    {
        var settings = PipelineFixWorkerSettings.FromEnvironment(read);
        var (commandRunner, pathValidator) = BuildGuards(settings.WorkspaceRoot);
        var repair = BuildRepairFactory(settings.LlmProvider, settings.LlmModel, settings.JobId, audit, http, read);

        var worker = new PipelineFixWorker(commandRunner, pathValidator, gitProvider, repair);
        var result = await worker.RunAsync(settings);
        Console.WriteLine($"[worker] job={result.JobId} type=PipelineFix status={result.Status} pr={result.PullRequestUrl} :: {result.Message}");
        exitCode = result.Status is DevAgent.Contracts.Jobs.AgentJobStatus.Failed ? 1 : 0;
    }
    else if (string.Equals(jobType, "DocUpdate", StringComparison.OrdinalIgnoreCase))
    {
        var settings = DocUpdateWorkerSettings.FromEnvironment(read);
        var (commandRunner, pathValidator) = BuildGuards(settings.WorkspaceRoot);

        // DocScribe's agent is write-scoped to docs/ + README.md by POLICY —
        // it is structurally unable to modify code.
        var authoring = BuildRepairFactory(settings.LlmProvider, settings.LlmModel, settings.JobId, audit, http, read,
            WriteScopePolicy.FromPrefixes(new[] { "docs/", "README.md" }));

        var worker = new DocScribeWorker(commandRunner, pathValidator, gitProvider, authoring);
        var result = await worker.RunAsync(settings);
        Console.WriteLine($"[worker] job={result.JobId} type=DocUpdate status={result.Status} pr={result.PullRequestUrl} :: {result.Message}");
        exitCode = result.Status is DevAgent.Contracts.Jobs.AgentJobStatus.Failed ? 1 : 0;
    }
    else if (string.Equals(jobType, "CodeReview", StringComparison.OrdinalIgnoreCase))
    {
        var settings = CodeReviewWorkerSettings.FromEnvironment(read);
        var (commandRunner, pathValidator) = BuildGuards(settings.WorkspaceRoot);

        // The review agent is fully READ-ONLY by policy; its only output is a comment.
        var reviewer = BuildRepairFactory(settings.LlmProvider, settings.LlmModel, settings.JobId, audit, http, read,
            WriteScopePolicy.ReadOnly);

        var worker = new CodeReviewWorker(commandRunner, pathValidator, gitProvider, reviewer);
        var result = await worker.RunAsync(settings);
        Console.WriteLine($"[worker] job={result.JobId} type=CodeReview status={result.Status} :: {result.Message}");
        exitCode = result.Status is DevAgent.Contracts.Jobs.AgentJobStatus.Failed ? 1 : 0;
    }
    else if (string.Equals(jobType, "DotNetUpgrade", StringComparison.OrdinalIgnoreCase))
    {
        var settings = DotNetUpgradeWorkerSettings.FromEnvironment(read);
        var (commandRunner, pathValidator) = BuildGuards(settings.WorkspaceRoot);
        var repair = BuildRepairFactory(settings.LlmProvider, settings.LlmModel, settings.JobId, audit, http, read);

        var worker = new DotNetUpgradeWorker(commandRunner, pathValidator, new TargetFrameworkUpdater(settings.OnlyUpgrade), gitProvider, repair);
        var result = await worker.RunAsync(settings);
        Console.WriteLine($"[worker] job={result.JobId} type=DotNetUpgrade status={result.Status} pr={result.PullRequestUrl} :: {result.Message}");
        exitCode = result.Status is DevAgent.Contracts.Jobs.AgentJobStatus.Failed ? 1 : 0;
    }
    else
    {
        var settings = WorkerJobSettings.FromEnvironment(read);
        var (commandRunner, pathValidator) = BuildGuards(settings.WorkspaceRoot);
        var repair = BuildRepairFactory(settings.LlmProvider, settings.LlmModel, settings.JobId, audit, http, read);

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
//
// MCP: when the Runner supplied a gateway + per-job token + granted tool
// descriptors, those tools are exposed to the model (with the servers' own
// schemas) and executed through the gateway — which re-validates every call.
static Func<string, ICodingAgent>? BuildRepairFactory(string? providerName, string? model, string jobId, IAuditLog audit, HttpClient http, Func<string, string?> read, WriteScopePolicy? writeScope = null)
{
    if (!Enum.TryParse<LlmProvider>(providerName, ignoreCase: true, out var provider))
    {
        return null;
    }

    var options = new LlmClientOptions { Provider = provider, Model = model };

    IMcpToolExecutor? mcpExecutor = null;
    var gatewayUrl = read("DEVAGENT_MCP_GATEWAY");
    var gatewayToken = read("DEVAGENT_MCP_TOKEN");
    if (!string.IsNullOrEmpty(gatewayUrl) && !string.IsNullOrEmpty(gatewayToken))
    {
        options.AdditionalTools = ParseMcpTools(read("DEVAGENT_MCP_TOOLS"));
        mcpExecutor = new McpGatewayToolExecutor(new GatewayMcpClient(http, gatewayUrl!, gatewayToken!));
    }

    var llm = LlmClientFactory.Create(options, http);
    var agentOptions = new CodingAgentOptions { WriteScope = writeScope ?? WriteScopePolicy.AllowAll };
    return repoPath => CodingAgentFactory.Create(repoPath, llm, audit, jobId, agentOptions, mcpExecutor: mcpExecutor);
}

// Parse the Runner-provided MCP tool descriptors into LLM tool schemas. The
// wire name is namespaced (mcp__{serverKey}__{tool}) so it can never collide
// with — or impersonate — a builtin tool.
static IReadOnlyList<LlmToolDescriptor> ParseMcpTools(string? json)
{
    if (string.IsNullOrWhiteSpace(json))
    {
        return Array.Empty<LlmToolDescriptor>();
    }

    try
    {
        var docs = JsonSerializer.Deserialize<List<McpToolEnv>>(json!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return (docs ?? new List<McpToolEnv>())
            .Where(d => !string.IsNullOrWhiteSpace(d.ServerKey) && !string.IsNullOrWhiteSpace(d.Name))
            .Select(d => new LlmToolDescriptor(
                $"mcp__{d.ServerKey}__{d.Name}",
                d.Description ?? string.Empty,
                string.IsNullOrWhiteSpace(d.InputSchemaJson) ? """{"type":"object"}""" : d.InputSchemaJson!))
            .ToList();
    }
    catch (JsonException)
    {
        return Array.Empty<LlmToolDescriptor>();
    }
}

internal sealed record McpToolEnv(string? ServerKey, string? Name, string? Description, string? InputSchemaJson);
