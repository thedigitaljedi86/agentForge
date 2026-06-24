using DevAgent.Audit;
using DevAgent.Contracts.Jobs;
using DevAgent.Guard.Policies;
using DevAgent.Runner.Api.Application;
using DevAgent.Runner.Api.Sandbox;

var builder = WebApplication.CreateBuilder(args);

// --- Configuration: allowlists come from configuration only ---
builder.Services.Configure<GuardPolicyOptions>(
    builder.Configuration.GetSection(GuardPolicyOptions.SectionName));

// Build the immutable policy set once at startup.
builder.Services.AddSingleton(sp =>
{
    var options = builder.Configuration
        .GetSection(GuardPolicyOptions.SectionName)
        .Get<GuardPolicyOptions>() ?? new GuardPolicyOptions();
    return options.Build();
});

builder.Services.AddSingleton<IAuditLog, ConsoleAuditLog>();
builder.Services.AddSingleton<ContainerImagePolicy>(sp => sp.GetRequiredService<GuardPolicySet>().ContainerImages);
builder.Services.AddSingleton<ISandboxJobRunner, PodmanSandboxJobRunner>();
builder.Services.AddScoped<RunnerJobApplicationService>();

// Swagger / OpenAPI for interactive local testing of the job endpoints.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
    c.SwaggerDoc("v1", new() { Title = "DevAgent.Runner", Version = "v1" }));

var app = builder.Build();

// SECURITY: only expose Swagger in Development. The launch profile sets the
// Development environment, so `dotnet run` shows it at /swagger.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Health check.
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "DevAgent.Runner" }));

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
