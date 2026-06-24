namespace DevAgent.Runner.Api.Sandbox;

using DevAgent.Contracts.Sandbox;

/// <summary>
/// Starts a sandbox worker for a fully-validated job. Implementations launch
/// an isolated container (Docker today, Kubernetes Jobs later) and surface the
/// worker's result.
///
/// SECURITY: Implementations receive an already-validated
/// <see cref="SandboxJobRequest"/> (allowlisted image, resolved clone URL).
/// They MUST NOT accept caller-supplied container arguments, mount host paths,
/// or expose the Docker socket to the worker.
/// </summary>
public interface ISandboxJobRunner
{
    Task<SandboxJobResult> RunAsync(SandboxJobRequest request, CancellationToken cancellationToken = default);
}
