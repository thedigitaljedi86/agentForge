using DevAgent.Audit;
using DevAgent.Hub.Api.Application;
using DevAgent.Hub.Api.Jobs;
using Hangfire;
using Hangfire.MemoryStorage;

var builder = WebApplication.CreateBuilder(args);

// --- Audit ---
builder.Services.AddSingleton<IAuditLog, ConsoleAuditLog>();

// --- Runner client (Hub -> Runner over HTTP) ---
var runnerBaseUrl = builder.Configuration["Runner:BaseUrl"] ?? "http://localhost:5081";
builder.Services.AddHttpClient<IRunnerClient, HttpRunnerClient>(client =>
{
    client.BaseAddress = new Uri(runnerBaseUrl);
});

// --- Application service (clean seam between API and workflow) ---
builder.Services.AddScoped<HubJobApplicationService>();

// --- Hangfire (in-memory for the first milestone) ---
builder.Services.AddHangfire(cfg => cfg
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseMemoryStorage());
builder.Services.AddHangfireServer();
builder.Services.AddScoped<PackageUpdateCheckJob>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "DevAgent.Hub" }));

// Hangfire dashboard (local only — secure properly before exposing).
app.UseHangfireDashboard("/hangfire");

// Placeholder recurring job: check for package updates every hour.
RecurringJob.AddOrUpdate<PackageUpdateCheckJob>(
    "dependencypilot-package-check",
    job => job.RunAsync(),
    Cron.Hourly);

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

app.Run();

/// <summary>Manual-trigger body. Repository + package are allowlist keys.</summary>
public sealed record StartNuGetUpdateHubRequest
{
    public required string RepositoryKey { get; init; }
    public required string PackageId { get; init; }
    public required string TargetVersion { get; init; }
    public string? RequestedBy { get; init; }
}

public partial class Program { }
