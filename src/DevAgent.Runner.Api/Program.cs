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

var app = builder.Build();

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

// Exposed for integration tests.
public partial class Program { }
