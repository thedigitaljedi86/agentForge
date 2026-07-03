namespace DevAgent.Runner.Tests;

using DevAgent.Audit;
using DevAgent.Contracts.Jobs;
using DevAgent.Contracts.Sandbox;
using DevAgent.Guard.Policies;
using DevAgent.Runner.Api.Sandbox;
using Xunit;

/// <summary>
/// These tests lock in the container hardening: the argument vector the runner
/// produces must always carry the isolation flags and must never carry mounts,
/// sockets, privileges or published ports.
/// </summary>
public class CliSandboxJobRunnerTests
{
    private const string Image = "registry/worker:8.0";

    private sealed class RecordingLauncher : ISandboxProcessLauncher
    {
        public string? LastCli { get; private set; }
        public IReadOnlyList<string>? LastArgs { get; private set; }
        public int ExitCode { get; set; }
        public string Stdout { get; set; } = "[worker] job=j status=Succeeded pr=x :: Pull request created.";

        public Task<SandboxProcessResult> LaunchAsync(string cli, IReadOnlyList<string> arguments, CancellationToken ct)
        {
            LastCli = cli;
            LastArgs = arguments;
            return Task.FromResult(new SandboxProcessResult(ExitCode, Stdout, ""));
        }
    }

    private static SandboxJobRequest Request(string image = Image) => new()
    {
        JobId = "job-1",
        JobType = AgentJobType.NuGetUpdate,
        CloneUrl = "https://git/svc-a.git",
        BaseBranch = "main",
        ContainerImage = image,
        PackageId = "Serilog",
        TargetVersion = "3.1.1",
    };

    private static (CliSandboxJobRunner runner, RecordingLauncher launcher) NewRunner(
        SandboxOptions? options = null)
    {
        var launcher = new RecordingLauncher();
        var runner = new CliSandboxJobRunner(
            new ContainerImagePolicy(new[] { Image }),
            options ?? new SandboxOptions { WorkerGitToken = "bot-token" },
            launcher,
            new ConsoleAuditLog());
        return (runner, launcher);
    }

    [Fact]
    public async Task Container_runs_with_hardening_flags_via_podman_by_default()
    {
        var (runner, launcher) = NewRunner();
        await runner.RunAsync(Request());

        Assert.Equal("podman", launcher.LastCli);
        var args = launcher.LastArgs!;
        Assert.Equal("run", args[0]);
        Assert.Contains("--rm", args);
        Assert.Contains("--cap-drop=ALL", args);
        Assert.Contains("no-new-privileges", args);
        Assert.Contains("--pids-limit", args);
        Assert.Contains("--memory", args);
        Assert.Contains("--cpus", args);
    }

    [Fact]
    public async Task No_mounts_sockets_privileges_or_ports_ever()
    {
        var (runner, launcher) = NewRunner();
        await runner.RunAsync(Request());

        foreach (var arg in launcher.LastArgs!)
        {
            Assert.DoesNotContain("docker.sock", arg);
            Assert.DoesNotContain("podman.sock", arg);
            Assert.NotEqual("-v", arg);
            Assert.NotEqual("--volume", arg);
            Assert.NotEqual("--mount", arg);
            Assert.NotEqual("--privileged", arg);
            Assert.NotEqual("-p", arg);
            Assert.NotEqual("--publish", arg);
            Assert.False(arg.StartsWith("--cap-add", StringComparison.Ordinal), $"unexpected cap-add: {arg}");
        }
    }

    [Fact]
    public async Task Image_is_the_final_argument_and_is_the_allowlisted_one()
    {
        var (runner, launcher) = NewRunner();
        await runner.RunAsync(Request());

        Assert.Equal(Image, launcher.LastArgs![^1]);
    }

    [Fact]
    public async Task Non_allowlisted_image_is_rejected_before_any_launch()
    {
        var (runner, launcher) = NewRunner();

        var result = await runner.RunAsync(Request(image: "attacker/evil:latest"));

        Assert.Equal(AgentJobStatus.Rejected, result.Status);
        Assert.Null(launcher.LastArgs); // the CLI was never invoked
    }

    [Fact]
    public async Task Malicious_values_stay_inside_single_vector_elements()
    {
        // Even if upstream validation failed and a hostile value reached the
        // runner, an argument VECTOR cannot grow new flags from a value.
        var (runner, launcher) = NewRunner();
        await runner.RunAsync(Request() with
        {
            PackageId = "Serilog --privileged -v /:/host",
        });

        var args = launcher.LastArgs!;
        Assert.DoesNotContain("--privileged", args);       // not a standalone element
        Assert.DoesNotContain("-v", args);
        var envElement = args.First(a => a.StartsWith("DEVAGENT_PACKAGE_ID=", StringComparison.Ordinal));
        Assert.Contains("--privileged", envElement);        // trapped inside the value
    }

    [Fact]
    public async Task Worker_exit_codes_map_to_job_status()
    {
        var (runner, launcher) = NewRunner();

        launcher.ExitCode = 0;
        Assert.Equal(AgentJobStatus.Succeeded, (await runner.RunAsync(Request())).Status);

        launcher.ExitCode = 1;
        Assert.Equal(AgentJobStatus.Failed, (await runner.RunAsync(Request())).Status);

        launcher.ExitCode = 2; // missing configuration
        Assert.Equal(AgentJobStatus.Failed, (await runner.RunAsync(Request())).Status);
    }

    [Fact]
    public async Task Job_settings_reach_the_worker_as_environment_variables_only()
    {
        var (runner, launcher) = NewRunner();
        await runner.RunAsync(Request());

        var args = launcher.LastArgs!;
        var envs = args.Where(a => a.StartsWith("DEVAGENT_", StringComparison.Ordinal)).ToList();

        Assert.Contains(envs, e => e == "DEVAGENT_JOB_TYPE=NuGetUpdate");
        Assert.Contains(envs, e => e == "DEVAGENT_JOB_ID=job-1");
        Assert.Contains(envs, e => e == "DEVAGENT_CLONE_URL=https://git/svc-a.git");
        Assert.Contains(envs, e => e == "DEVAGENT_PACKAGE_ID=Serilog");
        Assert.Contains(envs, e => e == "DEVAGENT_GIT_TOKEN=bot-token");
        // No positional command override: nothing after the image.
        Assert.Equal(Image, args[^1]);
    }

    [Fact]
    public async Task Llm_repair_model_is_operator_configuration_not_caller_input()
    {
        // Off by default: no provider env vars when the operator sets none.
        var (offRunner, offLauncher) = NewRunner();
        await offRunner.RunAsync(Request());
        Assert.DoesNotContain(offLauncher.LastArgs!, a => a.StartsWith("DEVAGENT_LLM_PROVIDER=", StringComparison.Ordinal));

        // On only via SandboxOptions (operator config).
        var (onRunner, onLauncher) = NewRunner(new SandboxOptions
        {
            WorkerGitToken = "bot-token",
            LlmProvider = "claude",
            LlmModel = "claude-opus-4-8",
        });
        await onRunner.RunAsync(Request());
        Assert.Contains(onLauncher.LastArgs!, a => a == "DEVAGENT_LLM_PROVIDER=claude");
        Assert.Contains(onLauncher.LastArgs!, a => a == "DEVAGENT_LLM_MODEL=claude-opus-4-8");
    }

    [Fact]
    public async Task Docker_cli_can_be_selected_by_operator_config()
    {
        var (runner, launcher) = NewRunner(new SandboxOptions { Cli = "docker", WorkerGitToken = "t" });
        await runner.RunAsync(Request());
        Assert.Equal("docker", launcher.LastCli);
    }
}
