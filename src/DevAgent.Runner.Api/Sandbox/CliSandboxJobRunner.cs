namespace DevAgent.Runner.Api.Sandbox;

using System.Diagnostics;
using DevAgent.Audit;
using DevAgent.Contracts.Jobs;
using DevAgent.Contracts.Sandbox;
using DevAgent.Guard.Policies;

/// <summary>Configuration for how sandbox containers are launched.</summary>
public sealed class SandboxOptions
{
    public const string SectionName = "Runner:Sandbox";

    /// <summary>"Stub" (default, no containers) or "Cli" (launch real containers).</summary>
    public string Mode { get; set; } = "Stub";

    /// <summary>
    /// Container CLI to invoke. Podman is the default: daemonless and rootless,
    /// so there is no privileged daemon socket to protect. "docker" is accepted
    /// for hosts that only have Docker.
    /// </summary>
    public string Cli { get; set; } = "podman";

    /// <summary>Container network ("bridge" allows git/feed access; "none" for air-gapped tests).</summary>
    public string Network { get; set; } = "bridge";

    public string Memory { get; set; } = "2g";
    public string Cpus { get; set; } = "2";

    /// <summary>Max processes inside the container (fork-bomb guard).</summary>
    public int PidsLimit { get; set; } = 512;

    /// <summary>
    /// Limited bot/service-account token handed to the worker for git push +
    /// PR creation. Configure via environment/secret store, never in source.
    /// </summary>
    public string WorkerGitToken { get; set; } = string.Empty;

    /// <summary>Workspace path inside the container.</summary>
    public string ContainerWorkspace { get; set; } = "/workspace";

    /// <summary>
    /// Fallback LLM provider/model for the worker's opt-in build-repair step
    /// when the agent has no pin of its own. Operator configuration — never
    /// caller input. Empty = repair disabled.
    /// </summary>
    public string LlmProvider { get; set; } = string.Empty;
    public string LlmModel { get; set; } = string.Empty;

    /// <summary>
    /// Base URL the SANDBOX uses to reach this Runner's MCP gateway (e.g.
    /// http://runner:8080). Empty = workers get no MCP access.
    /// </summary>
    public string McpGatewayBaseUrl { get; set; } = string.Empty;
}

/// <summary>
/// Abstraction over actually invoking the container CLI, so the runner's
/// argument construction can be tested without Podman/Docker installed.
/// </summary>
public interface ISandboxProcessLauncher
{
    Task<SandboxProcessResult> LaunchAsync(
        string cli,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

/// <summary>Exit code + output of a finished container run.</summary>
public sealed record SandboxProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Default launcher: spawns the CLI with an argument vector (no shell).</summary>
public sealed class ContainerProcessLauncher : ISandboxProcessLauncher
{
    public async Task<SandboxProcessResult> LaunchAsync(
        string cli, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = cli,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false, // SECURITY: never via a shell
            CreateNoWindow = true,
        };
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return new SandboxProcessResult(process.ExitCode, await stdout, await stderr);
    }
}

/// <summary>
/// Real sandbox runner: launches the worker in a hardened, throwaway container
/// via the podman (or docker) CLI.
///
/// SECURITY ENVELOPE (enforced by the fixed argument vector below):
///   * Only the allowlisted image runs (revalidated here, defence in depth).
///   * Job data reaches the worker via environment variables ONLY.
///   * NO volume/bind mounts — the worker clones into the container FS, which
///     `--rm` destroys afterwards. No container socket is ever mounted.
///   * `--cap-drop=ALL`, `--security-opt no-new-privileges`, pids/memory/cpu
///     limits; no `--privileged`, no port publishing, no host network.
///   * The argument list shape is FIXED. Caller-supplied strings are only ever
///     values inside single vector elements — never new flags — because the
///     process is started with an argument vector, not a shell string.
/// </summary>
public sealed class CliSandboxJobRunner : ISandboxJobRunner
{
    private readonly ContainerImagePolicy _imagePolicy;
    private readonly SandboxOptions _options;
    private readonly ISandboxProcessLauncher _launcher;
    private readonly IAuditLog _audit;

    public CliSandboxJobRunner(
        ContainerImagePolicy imagePolicy,
        SandboxOptions options,
        ISandboxProcessLauncher launcher,
        IAuditLog audit)
    {
        _imagePolicy = imagePolicy;
        _options = options;
        _launcher = launcher;
        _audit = audit;
    }

    /// <summary>Builds the exact container argument vector for a validated job.</summary>
    public IReadOnlyList<string> BuildContainerArguments(SandboxJobRequest request)
    {
        var args = new List<string>
        {
            "run",
            "--rm",
            "--cap-drop=ALL",
            "--security-opt", "no-new-privileges",
            "--pids-limit", _options.PidsLimit.ToString(),
            "--memory", _options.Memory,
            "--cpus", _options.Cpus,
            "--network", _options.Network,
        };

        void Env(string name, string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            args.Add("-e");
            args.Add($"{name}={value}");
        }

        Env("DEVAGENT_JOB_TYPE", request.JobType.ToString());
        Env("DEVAGENT_JOB_ID", request.JobId);
        Env("DEVAGENT_CLONE_URL", request.CloneUrl);
        Env("DEVAGENT_BASE_BRANCH", request.BaseBranch);
        Env("DEVAGENT_PACKAGE_ID", request.PackageId);
        Env("DEVAGENT_TARGET_VERSION", request.TargetVersion);
        Env("DEVAGENT_TARGET_FRAMEWORK", request.TargetFramework);
        Env("DEVAGENT_WORKSPACE", _options.ContainerWorkspace);
        Env("DEVAGENT_GIT_TOKEN", _options.WorkerGitToken);
        Env("DEVAGENT_ONLY_UPGRADE", request.OnlyUpgrade.ToString().ToLowerInvariant());

        // Repair model: the agent's admin-configured pin wins; the operator's
        // sandbox-level fallback applies otherwise. Never caller-chosen.
        Env("DEVAGENT_LLM_PROVIDER", string.IsNullOrEmpty(request.LlmProvider) ? _options.LlmProvider : request.LlmProvider);
        Env("DEVAGENT_LLM_MODEL", string.IsNullOrEmpty(request.LlmModel) ? _options.LlmModel : request.LlmModel);

        // MCP access: granted tool descriptors + a per-job gateway token. The
        // sandbox can only reach the GATEWAY — never an MCP server directly,
        // and never any server credential.
        if (!string.IsNullOrEmpty(_options.McpGatewayBaseUrl) && !string.IsNullOrEmpty(request.McpGatewayToken))
        {
            Env("DEVAGENT_MCP_GATEWAY", _options.McpGatewayBaseUrl);
            Env("DEVAGENT_MCP_TOKEN", request.McpGatewayToken);
            Env("DEVAGENT_MCP_TOOLS", request.McpToolsJson);
        }

        Env("DEVAGENT_SKILL_INSTRUCTIONS", request.SkillInstructions);

        // The image is always the LAST argument, and it is the allowlisted one.
        args.Add(request.ContainerImage);

        return args;
    }

    public async Task<SandboxJobResult> RunAsync(SandboxJobRequest request, CancellationToken cancellationToken = default)
    {
        // Defence in depth: revalidate the image even though the Runner's
        // application service already did.
        var imageCheck = _imagePolicy.Validate(request.ContainerImage);
        if (!imageCheck.IsValid)
        {
            await _audit.WriteAsync(new DecisionAuditEvent
            {
                JobId = request.JobId,
                Actor = nameof(CliSandboxJobRunner),
                Decision = "start-container",
                Allowed = false,
                Reason = imageCheck.Reason,
            }, cancellationToken);

            return new SandboxJobResult
            {
                JobId = request.JobId,
                Status = AgentJobStatus.Rejected,
                Message = imageCheck.Reason,
            };
        }

        var args = BuildContainerArguments(request);

        await _audit.WriteAsync(new DecisionAuditEvent
        {
            JobId = request.JobId,
            Actor = nameof(CliSandboxJobRunner),
            Decision = "start-container",
            Allowed = true,
            Reason = $"Launching image '{request.ContainerImage}' via {_options.Cli} with hardened flags (no mounts, no socket).",
        }, cancellationToken);

        var result = await _launcher.LaunchAsync(_options.Cli, args, cancellationToken).ConfigureAwait(false);

        // The worker's last stdout line carries its human-readable outcome.
        var tail = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? string.Empty;

        var status = result.ExitCode == 0 ? AgentJobStatus.Succeeded : AgentJobStatus.Failed;

        await _audit.WriteAsync(new JobAuditEvent
        {
            JobId = request.JobId,
            Actor = nameof(CliSandboxJobRunner),
            Status = status.ToString(),
            Message = $"Container exited with code {result.ExitCode}. {tail}",
        }, cancellationToken);

        return new SandboxJobResult
        {
            JobId = request.JobId,
            Status = status,
            Message = result.ExitCode == 0
                ? tail
                : $"Worker exited with code {result.ExitCode}. {tail} {Truncate(result.StandardError)}",
        };
    }

    private static string Truncate(string text, int max = 2000) =>
        text.Length <= max ? text : text[..max] + "…";
}
