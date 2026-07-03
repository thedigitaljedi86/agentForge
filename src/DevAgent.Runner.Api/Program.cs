using DevAgent.Audit;
using DevAgent.Bridge.Mcp;
using DevAgent.Contracts.Jobs;
using DevAgent.Guard.Policies;
using DevAgent.Runner.Api.Application;
using DevAgent.Runner.Api.Mcp;
using DevAgent.Runner.Api.Sandbox;
using DevAgent.Store;

var builder = WebApplication.CreateBuilder(args);

// --- Configuration: allowlists come from configuration only ---
builder.Services.Configure<GuardPolicyOptions>(
    builder.Configuration.GetSection(GuardPolicyOptions.SectionName));

builder.Services.AddSingleton<IAuditLog, ConsoleAuditLog>();

// --- Policy source ---
// With a configured database (ConnectionStrings:DevAgent) the SQLite store —
// which the admin console edits — is authoritative and read fresh per job.
// Without one, the appsettings Guard section applies, as before.
var storeEnabled = builder.Services.TryAddDevAgentStore(builder.Configuration);
if (storeEnabled)
{
    builder.Services.AddSingleton<IGuardPolicySource, StoreGuardPolicySource>();

    // --- MCP: gateway plumbing (tokens, client, per-job enrichment) ---
    builder.Services.AddSingleton<IMcpJobTokenStore, InMemoryMcpJobTokenStore>();
    builder.Services.AddHttpClient("mcp");
    builder.Services.AddSingleton<IMcpClient>(sp =>
    {
        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("mcp");
        var dbFactory = sp.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<DevAgentDbContext>>();
        return new HttpMcpClient(http, key =>
        {
            using var db = dbFactory.CreateDbContext();
            var entity = db.McpServers.FirstOrDefault(s => s.Key == key);
            return entity is null ? null : StoreSandboxJobEnricher.ToRegistration(entity);
        });
    });
    builder.Services.AddScoped<ISandboxJobEnricher, StoreSandboxJobEnricher>();
}
else
{
    builder.Services.AddSingleton<IGuardPolicySource>(_ =>
    {
        var options = builder.Configuration
            .GetSection(GuardPolicyOptions.SectionName)
            .Get<GuardPolicyOptions>() ?? new GuardPolicyOptions();
        return new ConfigGuardPolicySource(options);
    });
    builder.Services.AddSingleton<ISandboxJobEnricher, NullSandboxJobEnricher>();
}

// Snapshot image policy for the launcher's defence-in-depth re-check. The
// AUTHORITATIVE image check happens in the application service with a fresh
// policy read; this secondary check tolerates the startup snapshot.
builder.Services.AddSingleton<ContainerImagePolicy>(sp =>
    sp.GetRequiredService<IGuardPolicySource>().GetAsync().AsTask().GetAwaiter().GetResult().ContainerImages);

// --- Sandbox runner selection ---
// "Stub" (default) never starts containers; "Cli" launches hardened, throwaway
// worker containers via podman (or docker). The mode is operator configuration
// — callers can never influence it.
var sandboxOptions = builder.Configuration.GetSection(SandboxOptions.SectionName).Get<SandboxOptions>()
    ?? new SandboxOptions();
builder.Services.AddSingleton(sandboxOptions);
builder.Services.AddSingleton<ISandboxProcessLauncher, ContainerProcessLauncher>();
if (string.Equals(sandboxOptions.Mode, "Cli", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<ISandboxJobRunner, CliSandboxJobRunner>();
}
else
{
    builder.Services.AddSingleton<ISandboxJobRunner, PodmanSandboxJobRunner>();
}
builder.Services.AddScoped<RunnerJobApplicationService>();

// Swagger / OpenAPI for interactive local testing of the job endpoints.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
    c.SwaggerDoc("v1", new() { Title = "DevAgent.Runner", Version = "v1" }));

var app = builder.Build();

// Create/seed the store before the first policy read.
if (storeEnabled)
{
    using var scope = app.Services.CreateScope();
    var dbFactory = scope.ServiceProvider
        .GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<DevAgentDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();
    await StoreSetup.InitializeAsync(db, app.Configuration);
}

// SECURITY: only expose Swagger in Development. The launch profile sets the
// Development environment, so `dotnet run` shows it at /swagger.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Health check.
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "DevAgent.Runner" }));

// MCP gateway: the sandbox's only route to MCP servers (per-job tokens,
// registry ∩ grant validation, full audit). Requires the store.
if (storeEnabled)
{
    app.MapMcpGateway();
}

// SECURITY: This is the ONLY job-start endpoint. It accepts a typed
// NuGetUpdateJobRequest (keys, not URLs/images) and runs it through the full
// allowlist gate. There is deliberately NO generic "run command" endpoint.
app.MapPost("/runner/jobs/nuget-update", async (
    StartNuGetUpdateRunnerRequest body,
    RunnerJobApplicationService service,
    CancellationToken cancellationToken) =>
{
    var request = new NuGetUpdateJobRequest
    {
        JobId = body.JobId ?? Guid.NewGuid().ToString("N"),
        RepositoryKey = body.RepositoryKey,
        PackageId = body.PackageId,
        TargetVersion = body.TargetVersion,
        OnlyUpgrade = body.OnlyUpgrade,
        RequestedBy = body.RequestedBy ?? "hub",
    };

    var result = await service.StartNuGetUpdateAsync(request, cancellationToken);

    return result.Status == AgentJobStatus.Rejected
        ? Results.BadRequest(result)
        : Results.Ok(result);
});

// SECURITY: The .NET-upgrade job-start endpoint. It accepts a typed
// DotNetUpgradeJobRequest (key + framework, never a URL/image) and runs it
// through the full allowlist gate (job type, repository, target framework).
app.MapPost("/runner/jobs/dotnet-upgrade", async (
    StartDotNetUpgradeRunnerRequest body,
    RunnerJobApplicationService service,
    CancellationToken cancellationToken) =>
{
    var request = new DotNetUpgradeJobRequest
    {
        JobId = body.JobId ?? Guid.NewGuid().ToString("N"),
        RepositoryKey = body.RepositoryKey,
        TargetFramework = body.TargetFramework,
        OnlyUpgrade = body.OnlyUpgrade,
        RequestedBy = body.RequestedBy ?? "hub",
    };

    var result = await service.StartDotNetUpgradeAsync(request, cancellationToken);

    return result.Status == AgentJobStatus.Rejected
        ? Results.BadRequest(result)
        : Results.Ok(result);
});

app.Run();

/// <summary>
/// API body for starting a NuGet update job. Note the absence of any URL,
/// image or command field — only allowlist keys and the desired version.
/// </summary>
public sealed record StartNuGetUpdateRunnerRequest
{
    public string? JobId { get; init; }
    public required string RepositoryKey { get; init; }
    public required string PackageId { get; init; }
    public required string TargetVersion { get; init; }
    public bool OnlyUpgrade { get; init; } = true;
    public string? RequestedBy { get; init; }
}

/// <summary>
/// API body for starting a .NET-upgrade job. Like the NuGet body it carries only
/// an allowlist key and the desired framework — no URL, image or command.
/// </summary>
public sealed record StartDotNetUpgradeRunnerRequest
{
    public string? JobId { get; init; }
    public required string RepositoryKey { get; init; }
    public required string TargetFramework { get; init; }
    public bool OnlyUpgrade { get; init; } = true;
    public string? RequestedBy { get; init; }
}

// Exposed for integration tests.
public partial class Program { }
