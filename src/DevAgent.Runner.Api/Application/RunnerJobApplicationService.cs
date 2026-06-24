namespace DevAgent.Runner.Api.Application;

using DevAgent.Audit;
using DevAgent.Contracts.Jobs;
using DevAgent.Contracts.Sandbox;
using DevAgent.Contracts.Validation;
using DevAgent.Guard.Policies;
using DevAgent.Runner.Api.Sandbox;

/// <summary>
/// The Runner's application service: the LAST line of defence before a worker
/// runs. It performs the full allowlist validation and resolves caller-supplied
/// KEYS into trusted concrete values, then dispatches to the sandbox runner.
///
/// SECURITY: This is where the platform's core invariants are enforced:
///   * Job type must be allowlisted.
///   * Repository key must resolve from the repository allowlist (no raw URLs).
///   * Package id must be allowlisted.
///   * Container image is chosen from policy by job type (never from caller).
/// Any failure is audited and the job is rejected before a container starts.
/// </summary>
public sealed class RunnerJobApplicationService
{
    private readonly GuardPolicySet _policies;
    private readonly ISandboxJobRunner _sandboxRunner;
    private readonly IAuditLog _audit;

    public RunnerJobApplicationService(
        GuardPolicySet policies,
        ISandboxJobRunner sandboxRunner,
        IAuditLog audit)
    {
        _policies = policies;
        _sandboxRunner = sandboxRunner;
        _audit = audit;
    }

    public async Task<AgentJobResult> StartNuGetUpdateAsync(
        NuGetUpdateJobRequest request,
        CancellationToken cancellationToken = default)
    {
        // 1. Job type allowlist.
        var jobTypeCheck = _policies.JobTypes.Validate(request.JobType);
        if (!jobTypeCheck.IsValid)
        {
            return await RejectAsync(request.JobId, "job-type", jobTypeCheck, cancellationToken);
        }

        // 2. Repository allowlist (resolves a KEY, never a raw URL).
        var repoCheck = _policies.Repositories.Validate(request.RepositoryKey);
        if (!repoCheck.IsValid)
        {
            return await RejectAsync(request.JobId, "repository", repoCheck, cancellationToken);
        }

        // 3. Package allowlist.
        var packageCheck = _policies.Packages.Validate(request.PackageId);
        if (!packageCheck.IsValid)
        {
            return await RejectAsync(request.JobId, "package", packageCheck, cancellationToken);
        }

        // 4. Resolve trusted values from policy.
        var repository = _policies.Repositories.Resolve(request.RepositoryKey);
        var image = _policies.JobTypes.ResolveImage(request.JobType);

        // 5. Container image allowlist (defence in depth — the image came from
        //    policy, but we re-validate to keep this gate self-contained).
        var imageCheck = _policies.ContainerImages.Validate(image);
        if (!imageCheck.IsValid)
        {
            return await RejectAsync(request.JobId, "container-image", imageCheck, cancellationToken);
        }

        // 6. Build the fully-validated sandbox request and dispatch.
        var sandboxRequest = new SandboxJobRequest
        {
            JobId = request.JobId,
            JobType = request.JobType,
            CloneUrl = repository.CloneUrl,
            BaseBranch = repository.BaseBranch,
            ContainerImage = image,
            PackageId = request.PackageId,
            TargetVersion = request.TargetVersion,
            OnlyUpgrade = request.OnlyUpgrade,
        };

        await _audit.WriteAsync(new DecisionAuditEvent
        {
            JobId = request.JobId,
            Actor = nameof(RunnerJobApplicationService),
            Decision = "start-sandbox-job",
            Allowed = true,
            Reason = $"All allowlists passed for {request.RepositoryKey}/{request.PackageId}@{request.TargetVersion}.",
        }, cancellationToken);

        var sandboxResult = await _sandboxRunner.RunAsync(sandboxRequest, cancellationToken);

        return new AgentJobResult
        {
            JobId = sandboxResult.JobId,
            Status = sandboxResult.Status,
            Message = sandboxResult.Message,
            PullRequestUrl = sandboxResult.PullRequestUrl,
        };
    }

    private async Task<AgentJobResult> RejectAsync(
        string jobId, string gate, ValidationResult result, CancellationToken cancellationToken)
    {
        await _audit.WriteAsync(new DecisionAuditEvent
        {
            JobId = jobId,
            Actor = nameof(RunnerJobApplicationService),
            Decision = $"validate-{gate}",
            Allowed = false,
            Reason = result.Reason,
        }, cancellationToken);

        return AgentJobResult.Rejected(jobId, result.Reason ?? "Rejected by policy.");
    }
}
