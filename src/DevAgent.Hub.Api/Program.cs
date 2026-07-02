using Agents.DependencyPilot;
using Agents.DotNetUpgrader;
using DevAgent.Audit;
using DevAgent.Bridge.NuGet;
using DevAgent.Hub.Api.Application;
using DevAgent.Hub.Api.Jobs;
using Hangfire;
using Hangfire.MemoryStorage;

var builder = WebApplication.CreateBuilder(args);

// --- Audit + job status tracking (powers the dashboard) ---
builder.Services.AddSingleton<IAuditLog, ConsoleAuditLog>();
builder.Services.AddSingleton<IJobTracker, InMemoryJobTracker>();

// --- Runner client (Hub -> Runner over HTTP) ---
var runnerBaseUrl = builder.Configuration["Runner:BaseUrl"] ?? "http://localhost:5081";
builder.Services.AddHttpClient<IRunnerClient, HttpRunnerClient>(client =>
{
    client.BaseAddress = new Uri(runnerBaseUrl);
});

// --- Application service (clean seam between API and workflow) ---
builder.Services.AddScoped<HubJobApplicationService>();

// --- DotNetUpgrader agent (example of a scheduled agent) ---
builder.Services.Configure<DotNetUpgraderOptions>(
    builder.Configuration.GetSection(DotNetUpgraderOptions.SectionName));
builder.Services.AddScoped<IDotNetUpgradeTrigger, HubDotNetUpgradeTrigger>();
builder.Services.AddScoped<DotNetUpgradeService>();

// --- DependencyPilot agent + its NuGet bridges ---
builder.Services.Configure<DependencyPilotOptions>(
    builder.Configuration.GetSection(DependencyPilotOptions.SectionName));

builder.Services.AddSingleton(
    builder.Configuration.GetSection(NuGetFeedOptions.SectionName).Get<NuGetFeedOptions>()
        ?? new NuGetFeedOptions());
builder.Services.AddHttpClient<INuGetPackageProvider, HttpNuGetPackageProvider>();

builder.Services.AddSingleton(
    builder.Configuration.GetSection(PackageUsageMapOptions.SectionName).Get<PackageUsageMapOptions>()
        ?? new PackageUsageMapOptions());
builder.Services.AddSingleton<IPackageUsageScanner, ConfiguredPackageUsageScanner>();

builder.Services.AddScoped<IDependencyUpdateTrigger, HubDependencyUpdateTrigger>();
builder.Services.AddScoped<DependencyPilotService>();

// --- Hangfire (in-memory for the first milestone) ---
builder.Services.AddHangfire(cfg => cfg
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseMemoryStorage());
builder.Services.AddHangfireServer();
builder.Services.AddScoped<PackageUpdateCheckJob>();
builder.Services.AddScoped<ScheduledDotNetUpgradeJob>();

// Swagger / OpenAPI for interactive local testing of the manual-trigger endpoints.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
    c.SwaggerDoc("v1", new() { Title = "DevAgent.Hub", Version = "v1" }));

var app = builder.Build();

// SECURITY: only expose Swagger in Development. The launch profile sets the
// Development environment, so `dotnet run` shows it at /swagger.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Status dashboard: wwwroot/index.html served at "/".
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "DevAgent.Hub" }));

// Live job feed for the dashboard: which agents received tasks + their status.
app.MapGet("/jobs", (IJobTracker tracker) =>
    Results.Ok(tracker.Snapshot().Select(j => new
    {
        j.JobId,
        j.Agent,
        j.JobType,
        j.Target,
        status = (int)j.Status,
        statusName = j.Status.ToString(),
        j.Message,
        j.ReceivedAtUtc,
        j.UpdatedAtUtc,
    })));

// Hangfire dashboard (local only — secure properly before exposing).
app.UseHangfireDashboard("/hangfire");

// DependencyPilot: check watched packages for new versions every hour.
RecurringJob.AddOrUpdate<PackageUpdateCheckJob>(
    "dependencypilot-package-check",
    job => job.RunAsync(),
    Cron.Hourly);

// EXAMPLE scheduled agent: nightly .NET upgrade sweep of all watched repos.
RecurringJob.AddOrUpdate<ScheduledDotNetUpgradeJob>(
    "dotnetupgrader-nightly-sweep",
    job => job.RunAsync(),
    Cron.Daily);

// Kick one sweep on startup so the dashboard shows the scheduled agent's work
// immediately (rather than waiting for the nightly cron).
BackgroundJob.Enqueue<ScheduledDotNetUpgradeJob>(job => job.RunAsync());

// --- Manual trigger: start a DependencyPilot NuGet update job ---
// This is the API-layer endpoint; all real work is delegated to the
// application service, which forwards to the Runner for validation.
app.MapPost("/hub/dependencypilot/nuget-update", async (
    StartNuGetUpdateHubRequest body,
    HubJobApplicationService service,
    CancellationToken cancellationToken) =>
{
    var result = await service.StartNuGetUpdateAsync(
        body.RepositoryKey,
        body.PackageId,
        body.TargetVersion,
        body.RequestedBy ?? "manual",
        cancellationToken);

    return Results.Ok(result);
});

// --- Manual trigger: start a DotNetUpgrader framework-upgrade job ---
app.MapPost("/hub/dotnetupgrader/upgrade", async (
    StartDotNetUpgradeHubRequest body,
    HubJobApplicationService service,
    CancellationToken cancellationToken) =>
{
    var result = await service.StartDotNetUpgradeAsync(
        body.RepositoryKey,
        body.TargetFramework,
        body.RequestedBy ?? "manual",
        cancellationToken);

    return Results.Ok(result);
});

// --- Webhook: a feed announced a new package version ---
// SECURITY: The payload carries only a package id + version. Both are checked
// against DependencyPilot's watch lists before anything happens, and every
// resulting job still passes the Runner's allowlist gate. A webhook cannot
// name a repository, an image or a command.
app.MapPost("/hub/webhooks/nuget-package-published", async (
    PackagePublishedWebhook body,
    DependencyPilotService dependencyPilot,
    CancellationToken cancellationToken) =>
{
    var results = await dependencyPilot.HandlePackagePublishedAsync(
        body.PackageId, body.Version, cancellationToken);

    return Results.Ok(new
    {
        started = results.Count(r => r.Status != DevAgent.Contracts.Jobs.AgentJobStatus.Rejected),
        rejected = results.Count(r => r.Status == DevAgent.Contracts.Jobs.AgentJobStatus.Rejected),
        results,
    });
});

app.Run();

/// <summary>Webhook body: which package, which new version. Nothing else.</summary>
public sealed record PackagePublishedWebhook
{
    public required string PackageId { get; init; }
    public required string Version { get; init; }
}

/// <summary>Manual-trigger body. Repository + package are allowlist keys.</summary>
public sealed record StartNuGetUpdateHubRequest
{
    public required string RepositoryKey { get; init; }
    public required string PackageId { get; init; }
    public required string TargetVersion { get; init; }
    public string? RequestedBy { get; init; }
}

/// <summary>Manual-trigger body for a .NET upgrade. Repository is an allowlist key.</summary>
public sealed record StartDotNetUpgradeHubRequest
{
    public required string RepositoryKey { get; init; }
    public required string TargetFramework { get; init; }
    public string? RequestedBy { get; init; }
}

public partial class Program { }
