namespace DevAgent.Runner.Api.Sandbox;

using DevAgent.Audit;
using DevAgent.Contracts.Jobs;
using DevAgent.Contracts.Sandbox;
using DevAgent.Guard.Policies;

/// <summary>
/// First-milestone STUB for the Docker-backed sandbox runner. It does NOT start
/// a container yet; it documents and enforces the security envelope a real
/// implementation must honour, then returns a deterministic placeholder result.
///
/// SECURITY ENVELOPE the real implementation must implement:
///   * Run the allowlisted image ONLY (revalidated here, defence in depth).
///   * Pass the job to the worker via environment variables only.
///   * Do NOT bind-mount host paths; use a controlled, per-job temp workspace
///     volume that is removed afterwards.
///   * Do NOT mount the Docker socket into the worker.
///   * Drop capabilities, run as non-root, read-only rootfs where possible,
///     no extra --privileged / --cap-add, no host networking.
///   * Inject only a limited bot token; never deployment or cloud credentials.
///   * Caller-supplied container arguments are NOT accepted anywhere.
/// </summary>
public sealed class DockerSandboxJobRunner : ISandboxJobRunner
{
    private readonly ContainerImagePolicy _imagePolicy;
    private readonly IAuditLog _audit;

    public DockerSandboxJobRunner(ContainerImagePolicy imagePolicy, IAuditLog audit)
    {
        _imagePolicy = imagePolicy;
        _audit = audit;
    }

    public async Task<SandboxJobResult> RunAsync(SandboxJobRequest request, CancellationToken cancellationToken = default)
    {
        // Defence in depth: even though the Runner already validated the image,
        // re-check it here so this component is safe to call in isolation.
        var imageCheck = _imagePolicy.Validate(request.ContainerImage);
        if (!imageCheck.IsValid)
        {
            await _audit.WriteAsync(new DecisionAuditEvent
            {
                JobId = request.JobId,
                Actor = nameof(DockerSandboxJobRunner),
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

        await _audit.WriteAsync(new JobAuditEvent
        {
            JobId = request.JobId,
            Actor = nameof(DockerSandboxJobRunner),
            Status = "would-start-container",
            Message = $"STUB: would run image '{request.ContainerImage}' for {request.JobType} " +
                      "with env-only config, no host mounts, no docker socket.",
        }, cancellationToken);

        // STUB result — a real implementation would await container completion
        // and parse the worker's reported SandboxJobResult.
        return new SandboxJobResult
        {
            JobId = request.JobId,
            Status = AgentJobStatus.Validated,
            Message = "Docker sandbox runner is a stub in the first milestone; no container was started.",
        };
    }
}
