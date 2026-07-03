namespace Agents.PipelineDoctor;

using DevAgent.Bridge.Ci;
using DevAgent.Contracts.Jobs;
using Microsoft.Extensions.Options;

/// <summary>The outcome of one sweep over a repository's CI failures.</summary>
public sealed record PipelineDoctorFinding(string RepositoryKey, string RunId, string Branch, AgentJobResult Result);

/// <summary>
/// The PipelineDoctor agent. On every sweep it:
///   1. Looks up the admin-configured CI connection per watched repository
///      (GitHub Actions, GitLab CI or Azure DevOps Pipelines — one seam).
///   2. Lists recent FAILED runs (read-only; the agent can never trigger,
///      cancel or retry anything on the CI side).
///   3. Skips runs that were already handled (processed-run store).
///   4. Fetches the failure log tail and proposes a PipelineFix job — by
///      repository KEY, with the log as context only.
///
/// SECURITY: The CI token is referenced as an env-var NAME resolved by the CI
/// bridge on the Hub host; it never enters a sandbox. The Runner re-validates
/// every proposal (repo key, job type, branch ref-name) before anything runs.
/// </summary>
public sealed class PipelineDoctorService
{
    private readonly PipelineDoctorOptions _options;
    private readonly ICiConnectionSource _connections;
    private readonly IProcessedRunStore _processed;
    private readonly CiProviderFactory _providers;
    private readonly IPipelineFixTrigger _trigger;

    public PipelineDoctorService(
        IOptions<PipelineDoctorOptions> options,
        ICiConnectionSource connections,
        IProcessedRunStore processed,
        CiProviderFactory providers,
        IPipelineFixTrigger trigger)
    {
        _options = options.Value;
        _connections = connections;
        _processed = processed;
        _providers = providers;
        _trigger = trigger;
    }

    /// <summary>Sweep every watched repository for new pipeline failures.</summary>
    public async Task<IReadOnlyList<PipelineDoctorFinding>> SweepAsync(CancellationToken cancellationToken = default)
    {
        var findings = new List<PipelineDoctorFinding>();
        foreach (var repositoryKey in _options.RepositoryKeys)
        {
            findings.AddRange(await SweepRepositoryAsync(repositoryKey, cancellationToken));
        }

        return findings;
    }

    /// <summary>
    /// Sweep one repository (also the manual-trigger path). The repository must
    /// be on THIS AGENT's watch list — the Runner's allowlist still applies on
    /// top afterwards.
    /// </summary>
    public async Task<IReadOnlyList<PipelineDoctorFinding>> SweepRepositoryAsync(
        string repositoryKey, CancellationToken cancellationToken = default)
    {
        if (!_options.RepositoryKeys.Contains(repositoryKey, StringComparer.OrdinalIgnoreCase))
        {
            return new[]
            {
                new PipelineDoctorFinding(repositoryKey, RunId: "-", Branch: "-",
                    AgentJobResult.Rejected(Guid.NewGuid().ToString("N"),
                        $"Repository '{repositoryKey}' is not watched by PipelineDoctor.")),
            };
        }

        var connection = await _connections.GetAsync(repositoryKey, cancellationToken);
        if (connection is null)
        {
            // No CI connection configured for this repository — nothing to do.
            return Array.Empty<PipelineDoctorFinding>();
        }

        var provider = _providers.Create(connection.Provider);
        var failedRuns = await provider.ListFailedRunsAsync(connection, _options.RunsPerSweep, cancellationToken);

        var findings = new List<PipelineDoctorFinding>();
        foreach (var run in failedRuns)
        {
            if (await _processed.IsProcessedAsync(repositoryKey, run.RunId, cancellationToken))
            {
                continue;
            }

            var log = await provider.GetFailureLogAsync(connection, run.RunId, cancellationToken);

            var result = await _trigger.StartPipelineFixAsync(new PipelineFixJobRequest
            {
                RepositoryKey = repositoryKey,
                Branch = run.Branch,
                FailureContext = log,
                RequestedBy = "PipelineDoctor",
            }, cancellationToken);

            // Mark the run handled regardless of outcome: a rejected or failed
            // proposal will not improve by re-proposing the same run forever.
            await _processed.MarkProcessedAsync(repositoryKey, run.RunId, cancellationToken);

            findings.Add(new PipelineDoctorFinding(repositoryKey, run.RunId, run.Branch, result));
        }

        return findings;
    }
}
