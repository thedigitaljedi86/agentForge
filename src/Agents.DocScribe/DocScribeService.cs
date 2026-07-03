namespace Agents.DocScribe;

using DevAgent.Contracts.Jobs;
using Microsoft.Extensions.Options;

/// <summary>
/// The DocScribe agent: keeps repository documentation alive. On every
/// scheduled run (and on manual triggers) it proposes a DocUpdate job per
/// watched repository. The sandbox worker then regenerates the deterministic
/// code map and lets the docs-scoped agent refresh the prose — the result is a
/// review-required PR only when documentation actually changed.
///
/// SECURITY: DocScribe proposes work by repository KEY only. Its in-sandbox
/// authoring agent is write-scoped to docs/ + README.md by policy; even a
/// hostile instruction inside the repository cannot make it touch code.
/// </summary>
public sealed class DocScribeService
{
    private readonly DocScribeOptions _options;
    private readonly IDocUpdateTrigger _trigger;

    public DocScribeService(IOptions<DocScribeOptions> options, IDocUpdateTrigger trigger)
    {
        _options = options.Value;
        _trigger = trigger;
    }

    /// <summary>Refresh documentation for every watched repository (scheduled entry point).</summary>
    public async Task<IReadOnlyList<AgentJobResult>> SweepAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<AgentJobResult>();
        foreach (var repositoryKey in _options.RepositoryKeys)
        {
            results.Add(await StartDocUpdateWorkflowAsync(repositoryKey, cancellationToken));
        }

        return results;
    }

    /// <summary>Refresh documentation for one repository (manual entry point).</summary>
    public async Task<AgentJobResult> StartDocUpdateWorkflowAsync(
        string repositoryKey, CancellationToken cancellationToken = default)
    {
        if (!_options.RepositoryKeys.Contains(repositoryKey, StringComparer.OrdinalIgnoreCase))
        {
            return AgentJobResult.Rejected(
                Guid.NewGuid().ToString("N"),
                $"Repository '{repositoryKey}' is not watched by DocScribe.");
        }

        return await _trigger.StartDocUpdateAsync(new DocUpdateJobRequest
        {
            RepositoryKey = repositoryKey,
            RequestedBy = "DocScribe",
        }, cancellationToken);
    }
}
