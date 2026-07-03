namespace DevAgent.Runner.Api.Application;

using DevAgent.Audit;
using DevAgent.Contracts.Jobs;
using DevAgent.Contracts.Sandbox;
using DevAgent.Contracts.Validation;
using DevAgent.Guard.Policies;
using DevAgent.Runner.Api.Mcp;
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
    private readonly IGuardPolicySource _policySource;
    private readonly ISandboxJobRunner _sandboxRunner;
    private readonly IAuditLog _audit;
    private readonly ISandboxJobEnricher _enricher;

    public RunnerJobApplicationService(
        IGuardPolicySource policySource,
        ISandboxJobRunner sandboxRunner,
        IAuditLog audit,
        ISandboxJobEnricher? enricher = null)
    {
        _policySource = policySource;
        _sandboxRunner = sandboxRunner;
        _audit = audit;
        _enricher = enricher ?? new NullSandboxJobEnricher();
    }

    public async Task<AgentJobResult> StartNuGetUpdateAsync(
        NuGetUpdateJobRequest request,
        CancellationToken cancellationToken = default)
    {
        var _policies = await _policySource.GetAsync(cancellationToken);

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

        // Attach the requesting agent's capabilities (LLM pin, MCP grants,
        // skills) — resolved from the admin store, never from the caller.
        sandboxRequest = await _enricher.EnrichAsync(sandboxRequest, request.RequestedBy, cancellationToken);

        var sandboxResult = await _sandboxRunner.RunAsync(sandboxRequest, cancellationToken);

        return new AgentJobResult
        {
            JobId = sandboxResult.JobId,
            Status = sandboxResult.Status,
            Message = sandboxResult.Message,
            PullRequestUrl = sandboxResult.PullRequestUrl,
        };
    }

    public async Task<AgentJobResult> StartDotNetUpgradeAsync(
        DotNetUpgradeJobRequest request,
        CancellationToken cancellationToken = default)
    {
        var _policies = await _policySource.GetAsync(cancellationToken);

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

        // 3. Target framework allowlist + format.
        var frameworkCheck = _policies.TargetFrameworks.Validate(request.TargetFramework);
        if (!frameworkCheck.IsValid)
        {
            return await RejectAsync(request.JobId, "target-framework", frameworkCheck, cancellationToken);
        }

        // 4. Resolve trusted values from policy.
        var repository = _policies.Repositories.Resolve(request.RepositoryKey);
        var image = _policies.JobTypes.ResolveImage(request.JobType);

        // 5. Container image allowlist (defence in depth).
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
            TargetFramework = request.TargetFramework,
            OnlyUpgrade = request.OnlyUpgrade,
        };

        await _audit.WriteAsync(new DecisionAuditEvent
        {
            JobId = request.JobId,
            Actor = nameof(RunnerJobApplicationService),
            Decision = "start-sandbox-job",
            Allowed = true,
            Reason = $"All allowlists passed for {request.RepositoryKey} -> {request.TargetFramework}.",
        }, cancellationToken);

        // Attach the requesting agent's capabilities (LLM pin, MCP grants,
        // skills) — resolved from the admin store, never from the caller.
        sandboxRequest = await _enricher.EnrichAsync(sandboxRequest, request.RequestedBy, cancellationToken);

        var sandboxResult = await _sandboxRunner.RunAsync(sandboxRequest, cancellationToken);

        return new AgentJobResult
        {
            JobId = sandboxResult.JobId,
            Status = sandboxResult.Status,
            Message = sandboxResult.Message,
            PullRequestUrl = sandboxResult.PullRequestUrl,
        };
    }

    /// <summary>
    /// PipelineFix: repair a failing CI pipeline. The failing BRANCH comes
    /// from CI (external input) and is validated as a conservative ref name;
    /// the failure log is context only — it carries no authority anywhere.
    /// </summary>
    public async Task<AgentJobResult> StartPipelineFixAsync(
        PipelineFixJobRequest request,
        CancellationToken cancellationToken = default)
    {
        var policies = await _policySource.GetAsync(cancellationToken);

        var gate = await ValidateCommonAsync(policies, request.JobType, request.RepositoryKey, request.JobId, cancellationToken);
        if (gate.Rejection is not null)
        {
            return gate.Rejection;
        }

        var branchCheck = RefNamePolicy.Validate(request.Branch);
        if (!branchCheck.IsValid)
        {
            return await RejectAsync(request.JobId, "branch-name", branchCheck, cancellationToken);
        }

        var sandboxRequest = new SandboxJobRequest
        {
            JobId = request.JobId,
            JobType = request.JobType,
            CloneUrl = gate.Repository!.CloneUrl,
            // The job starts FROM the failing branch, not the repo default.
            BaseBranch = request.Branch,
            ContainerImage = gate.Image!,
            FailureContext = Truncate(request.FailureContext, 12_000),
        };

        return await DispatchAsync(request.JobId, sandboxRequest, request.RequestedBy,
            $"All allowlists passed for {request.RepositoryKey} (failing branch '{request.Branch}').", cancellationToken);
    }

    /// <summary>DocUpdate: generate/refresh documentation for a repository.</summary>
    public async Task<AgentJobResult> StartDocUpdateAsync(
        DocUpdateJobRequest request,
        CancellationToken cancellationToken = default)
    {
        var policies = await _policySource.GetAsync(cancellationToken);

        var gate = await ValidateCommonAsync(policies, request.JobType, request.RepositoryKey, request.JobId, cancellationToken);
        if (gate.Rejection is not null)
        {
            return gate.Rejection;
        }

        var sandboxRequest = new SandboxJobRequest
        {
            JobId = request.JobId,
            JobType = request.JobType,
            CloneUrl = gate.Repository!.CloneUrl,
            BaseBranch = gate.Repository.BaseBranch,
            ContainerImage = gate.Image!,
        };

        return await DispatchAsync(request.JobId, sandboxRequest, request.RequestedBy,
            $"All allowlists passed for {request.RepositoryKey} (documentation update).", cancellationToken);
    }

    /// <summary>
    /// CodeReview: review a PR's changes. The source branch is external input
    /// (PR webhook) and is validated as a conservative ref name. The review
    /// worker never pushes — its only output is a PR comment.
    /// </summary>
    public async Task<AgentJobResult> StartCodeReviewAsync(
        CodeReviewJobRequest request,
        CancellationToken cancellationToken = default)
    {
        var policies = await _policySource.GetAsync(cancellationToken);

        var gate = await ValidateCommonAsync(policies, request.JobType, request.RepositoryKey, request.JobId, cancellationToken);
        if (gate.Rejection is not null)
        {
            return gate.Rejection;
        }

        var branchCheck = RefNamePolicy.Validate(request.SourceBranch);
        if (!branchCheck.IsValid)
        {
            return await RejectAsync(request.JobId, "branch-name", branchCheck, cancellationToken);
        }

        var sandboxRequest = new SandboxJobRequest
        {
            JobId = request.JobId,
            JobType = request.JobType,
            CloneUrl = gate.Repository!.CloneUrl,
            BaseBranch = gate.Repository.BaseBranch,
            ContainerImage = gate.Image!,
            SourceBranch = request.SourceBranch,
            PrNumber = request.PrNumber,
        };

        return await DispatchAsync(request.JobId, sandboxRequest, request.RequestedBy,
            $"All allowlists passed for {request.RepositoryKey} (review of '{request.SourceBranch}').", cancellationToken);
    }

    // ---- shared gate + dispatch for the task-style jobs ----

    private sealed record CommonGate(AgentJobResult? Rejection, RepositoryEntry? Repository, string? Image);

    /// <summary>Job-type + repository + image allowlists — identical for every job.</summary>
    private async Task<CommonGate> ValidateCommonAsync(
        GuardPolicySet policies, AgentJobType jobType, string repositoryKey, string jobId, CancellationToken cancellationToken)
    {
        var jobTypeCheck = policies.JobTypes.Validate(jobType);
        if (!jobTypeCheck.IsValid)
        {
            return new CommonGate(await RejectAsync(jobId, "job-type", jobTypeCheck, cancellationToken), null, null);
        }

        var repoCheck = policies.Repositories.Validate(repositoryKey);
        if (!repoCheck.IsValid)
        {
            return new CommonGate(await RejectAsync(jobId, "repository", repoCheck, cancellationToken), null, null);
        }

        var repository = policies.Repositories.Resolve(repositoryKey);
        var image = policies.JobTypes.ResolveImage(jobType);

        var imageCheck = policies.ContainerImages.Validate(image);
        if (!imageCheck.IsValid)
        {
            return new CommonGate(await RejectAsync(jobId, "container-image", imageCheck, cancellationToken), null, null);
        }

        return new CommonGate(null, repository, image);
    }

    private async Task<AgentJobResult> DispatchAsync(
        string jobId, SandboxJobRequest sandboxRequest, string requestedBy, string reason, CancellationToken cancellationToken)
    {
        await _audit.WriteAsync(new DecisionAuditEvent
        {
            JobId = jobId,
            Actor = nameof(RunnerJobApplicationService),
            Decision = "start-sandbox-job",
            Allowed = true,
            Reason = reason,
        }, cancellationToken);

        // Attach the requesting agent's capabilities (LLM pin, MCP grants,
        // skills) — resolved from the admin store, never from the caller.
        sandboxRequest = await _enricher.EnrichAsync(sandboxRequest, requestedBy, cancellationToken);

        var sandboxResult = await _sandboxRunner.RunAsync(sandboxRequest, cancellationToken);

        return new AgentJobResult
        {
            JobId = sandboxResult.JobId,
            Status = sandboxResult.Status,
            Message = sandboxResult.Message,
            PullRequestUrl = sandboxResult.PullRequestUrl,
        };
    }

    private static string? Truncate(string? text, int max) =>
        text is null || text.Length <= max ? text : text[^max..];

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
