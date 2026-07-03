using Agents.CodeReviewer;
using Agents.ConfluenceGuide;
using Agents.DependencyPilot;
using Agents.DocScribe;
using Agents.DotNetUpgrader;
using Agents.PipelineDoctor;
using Agents.SplunkSentinel;
using DevAgent.Audit;
using DevAgent.Bridge.Ci;
using DevAgent.Bridge.Confluence;
using DevAgent.Bridge.NuGet;
using DevAgent.Bridge.Splunk;
using DevAgent.Hub.Api.Admin;
using DevAgent.Hub.Api.Application;
using DevAgent.Hub.Api.Jobs;
using DevAgent.Store;
using Hangfire;
using Hangfire.MemoryStorage;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// --- Audit + job status tracking (powers the dashboards) ---
// Console = durable-ish log stream; ring buffer = the admin console's live
// audit window. Both receive every event.
builder.Services.AddSingleton<InMemoryRingAuditLog>();
builder.Services.AddSingleton<IAuditLog>(sp =>
    new CompositeAuditLog(new ConsoleAuditLog(), sp.GetRequiredService<InMemoryRingAuditLog>()));
builder.Services.AddSingleton<IJobTracker, InMemoryJobTracker>();

// --- Admin store (SQLite) + admin console auth ---
// With ConnectionStrings:DevAgent set, the store is the source of truth for
// allowlists, agent settings, MCP servers, skills and webhooks — and the
// admin console at /admin (cookie login) is how it changes.
var storeEnabled = builder.Services.TryAddDevAgentStore(builder.Configuration);
if (storeEnabled)
{
    builder.Services.AddAdminAuth();
    builder.Services.AddStoreBackedAgentOptions();
}

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
if (!storeEnabled)
{
    builder.Services.AddSingleton<IPackageUsageScanner, ConfiguredPackageUsageScanner>();
}

builder.Services.AddScoped<IDependencyUpdateTrigger, HubDependencyUpdateTrigger>();
builder.Services.AddScoped<DependencyPilotService>();

// --- PipelineDoctor agent (multi-provider CI watcher) ---
builder.Services.Configure<PipelineDoctorOptions>(
    builder.Configuration.GetSection(PipelineDoctorOptions.SectionName));
builder.Services.AddHttpClient("ci");
builder.Services.AddSingleton(sp =>
    new CiProviderFactory(sp.GetRequiredService<IHttpClientFactory>().CreateClient("ci")));
if (storeEnabled)
{
    builder.Services.AddScoped<ICiConnectionSource, StoreCiConnectionSource>();
    builder.Services.AddScoped<IProcessedRunStore, StoreProcessedRunStore>();
}
else
{
    builder.Services.AddSingleton<ICiConnectionSource, NullCiConnectionSource>();
    builder.Services.AddSingleton<IProcessedRunStore, InMemoryProcessedRunStore>();
}

builder.Services.AddScoped<IPipelineFixTrigger, HubPipelineFixTrigger>();
builder.Services.AddScoped<PipelineDoctorService>();

// --- DocScribe agent (scheduled documentation maintenance) ---
builder.Services.Configure<DocScribeOptions>(
    builder.Configuration.GetSection(DocScribeOptions.SectionName));
builder.Services.AddScoped<IDocUpdateTrigger, HubDocUpdateTrigger>();
builder.Services.AddScoped<DocScribeService>();

// --- CodeReviewer agent (PR-opened webhook -> read-only review) ---
builder.Services.Configure<CodeReviewerOptions>(
    builder.Configuration.GetSection(CodeReviewerOptions.SectionName));
builder.Services.AddScoped<ICodeReviewTrigger, HubCodeReviewTrigger>();
builder.Services.AddScoped<CodeReviewerService>();

// --- SplunkSentinel (observer) + ConfluenceGuide (docs sync planner) ---
builder.Services.Configure<SplunkSentinelOptions>(
    builder.Configuration.GetSection(SplunkSentinelOptions.SectionName));
builder.Services.AddHttpClient<ISplunkSearchClient, HttpSplunkSearchClient>();
builder.Services.AddScoped<SplunkSentinelService>();

builder.Services.Configure<ConfluenceGuideOptions>(
    builder.Configuration.GetSection(ConfluenceGuideOptions.SectionName));
builder.Services.AddHttpClient<IConfluenceClient, HttpConfluenceClient>();
builder.Services.AddScoped<ConfluenceGuideService>();

// Store-backed agent options must be registered AFTER the config bindings
// above so they take precedence when the store is enabled.
if (storeEnabled)
{
    builder.Services.AddStoreBackedAgentOptions();
}

// --- Hangfire (in-memory for the first milestone) ---
builder.Services.AddHangfire(cfg => cfg
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseMemoryStorage());
builder.Services.AddHangfireServer();
builder.Services.AddScoped<PackageUpdateCheckJob>();
builder.Services.AddScoped<ScheduledDotNetUpgradeJob>();
builder.Services.AddScoped<PipelineDoctorSweepJob>();
builder.Services.AddScoped<ScheduledDocUpdateJob>();
builder.Services.AddScoped<SplunkSentinelSweepJob>();
builder.Services.AddScoped<ConfluenceSyncPlanJob>();

// Swagger / OpenAPI for interactive local testing of the manual-trigger endpoints.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
    c.SwaggerDoc("v1", new() { Title = "DevAgent.Hub", Version = "v1" }));

var app = builder.Build();

// Create/seed the store and the admin user before anything reads them.
if (storeEnabled)
{
    using var scope = app.Services.CreateScope();
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DevAgentDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();
    await StoreSetup.InitializeAsync(db, app.Configuration);
    await AdminAuth.EnsureAdminUserAsync(db, app.Configuration);
}

// SECURITY: only expose Swagger in Development. The launch profile sets the
// Development environment, so `dotnet run` shows it at /swagger.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Status dashboard: wwwroot/index.html served at "/";
// admin console: wwwroot/admin/index.html served at /admin/.
app.UseDefaultFiles();
app.UseStaticFiles();

if (storeEnabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapAdminAuthEndpoints();
    app.MapAdminEndpoints();
}

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

// Hangfire dashboard: admin login required outside Development.
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = new[] { new HangfireDashboardAuthFilter(app.Environment.IsDevelopment()) },
});

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

// PipelineDoctor: check watched repositories' CI for new failures twice an hour.
RecurringJob.AddOrUpdate<PipelineDoctorSweepJob>(
    "pipelinedoctor-ci-sweep",
    job => job.RunAsync(),
    "*/30 * * * *");

// DocScribe: weekly documentation-maintenance sweep (Monday 06:00).
RecurringJob.AddOrUpdate<ScheduledDocUpdateJob>(
    "docscribe-weekly-docs",
    job => job.RunAsync(),
    "0 6 * * 1");

// SplunkSentinel: hourly observation sweep (no-op without configured searches).
RecurringJob.AddOrUpdate<SplunkSentinelSweepJob>(
    "splunksentinel-hourly-sweep",
    job => job.RunAsync(),
    Cron.Hourly);

// ConfluenceGuide: daily docs→page sync plan (read-only; no-op unconfigured).
RecurringJob.AddOrUpdate<ConfluenceSyncPlanJob>(
    "confluenceguide-daily-plan",
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
    HttpContext http,
    DependencyPilotService dependencyPilot,
    IServiceProvider services,
    CancellationToken cancellationToken) =>
{
    // Admin-managed webhook config: the hook can be disabled outright, and a
    // shared secret (X-DevAgent-Secret) can be required.
    var dbFactory = services.GetService<IDbContextFactory<DevAgentDbContext>>();
    if (dbFactory is not null)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var hook = await db.Webhooks.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Key == "nuget-package-published", cancellationToken);
        if (hook is { Enabled: false })
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (!string.IsNullOrEmpty(hook?.SharedSecret)
            && http.Request.Headers["X-DevAgent-Secret"].ToString() != hook.SharedSecret)
        {
            return Results.Unauthorized();
        }
    }

    var results = await dependencyPilot.HandlePackagePublishedAsync(
        body.PackageId, body.Version, cancellationToken);

    return Results.Ok(new
    {
        started = results.Count(r => r.Status != DevAgent.Contracts.Jobs.AgentJobStatus.Rejected),
        rejected = results.Count(r => r.Status == DevAgent.Contracts.Jobs.AgentJobStatus.Rejected),
        results,
    });
});

// --- Manual trigger: sweep one repository's CI for failures now ---
app.MapPost("/hub/pipelinedoctor/fix", async (
    StartPipelineFixHubRequest body,
    PipelineDoctorService pipelineDoctor,
    CancellationToken cancellationToken) =>
{
    var findings = await pipelineDoctor.SweepRepositoryAsync(body.RepositoryKey, cancellationToken);
    return Results.Ok(new
    {
        handled = findings.Count,
        findings = findings.Select(f => new { f.RepositoryKey, f.RunId, f.Branch, f.Result }),
    });
});

// --- Manual trigger: refresh one repository's documentation now ---
app.MapPost("/hub/docscribe/update", async (
    StartDocUpdateHubRequest body,
    DocScribeService docScribe,
    CancellationToken cancellationToken) =>
{
    var result = await docScribe.StartDocUpdateWorkflowAsync(body.RepositoryKey, cancellationToken);
    return Results.Ok(result);
});

// --- Manual trigger: review a PR now ---
app.MapPost("/hub/codereviewer/review", async (
    PullRequestOpenedWebhook body,
    CodeReviewerService codeReviewer,
    CancellationToken cancellationToken) =>
{
    var result = await codeReviewer.HandlePullRequestOpenedAsync(
        body.RepositoryKey, body.SourceBranch, body.PrNumber, cancellationToken);
    return Results.Ok(result);
});

// --- Webhook: a pull request was opened on a watched repository ---
// SECURITY: The payload carries only a repository KEY, a branch name and a PR
// number. The key must be on CodeReviewer's watch list, the Runner re-checks
// the key against the allowlist and the branch against the ref-name filter,
// and the review agent is read-only — a webhook can never cause a code change.
app.MapPost("/hub/webhooks/pull-request-opened", async (
    PullRequestOpenedWebhook body,
    HttpContext http,
    CodeReviewerService codeReviewer,
    IServiceProvider services,
    CancellationToken cancellationToken) =>
{
    var dbFactory = services.GetService<IDbContextFactory<DevAgentDbContext>>();
    if (dbFactory is not null)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var hook = await db.Webhooks.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Key == "pull-request-opened", cancellationToken);
        if (hook is { Enabled: false })
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (!string.IsNullOrEmpty(hook?.SharedSecret)
            && http.Request.Headers["X-DevAgent-Secret"].ToString() != hook.SharedSecret)
        {
            return Results.Unauthorized();
        }
    }

    var result = await codeReviewer.HandlePullRequestOpenedAsync(
        body.RepositoryKey, body.SourceBranch, body.PrNumber, cancellationToken);

    return Results.Ok(result);
});

// --- Manual trigger: run the Splunk searches / Confluence sync plan now ---
app.MapPost("/hub/splunksentinel/sweep", async (
    SplunkSentinelService sentinel,
    CancellationToken cancellationToken) =>
        Results.Ok(await sentinel.SweepAsync(cancellationToken)));

app.MapPost("/hub/confluenceguide/plan", async (
    ConfluenceGuideService guide,
    CancellationToken cancellationToken) =>
        Results.Ok(await guide.PlanSyncAsync(cancellationToken)));

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

/// <summary>PR webhook / manual review body: key + branch + PR number. Nothing else.</summary>
public sealed record PullRequestOpenedWebhook
{
    public required string RepositoryKey { get; init; }
    public required string SourceBranch { get; init; }
    public int? PrNumber { get; init; }
}

/// <summary>Manual-trigger body: sweep this repository's CI for failures.</summary>
public sealed record StartPipelineFixHubRequest
{
    public required string RepositoryKey { get; init; }
}

/// <summary>Manual-trigger body: refresh this repository's documentation.</summary>
public sealed record StartDocUpdateHubRequest
{
    public required string RepositoryKey { get; init; }
}

public partial class Program { }
