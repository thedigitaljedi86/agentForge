namespace Agents.DotNetUpgrader;

using DevAgent.Contracts.Jobs;
using Microsoft.Extensions.Options;

/// <summary>One proposed upgrade: move a repository's projects to a target framework.</summary>
public sealed record DotNetUpgradeCandidate(string RepositoryKey, string TargetFramework);

/// <summary>
/// The DotNetUpgrader agent. Responsibilities:
///   * Plan an upgrade per watched repository to the configured target framework.
///   * Start a platform workflow per repo (via the trigger seam).
///
/// SECURITY: DotNetUpgrader never clones, never runs commands and never opens
/// PRs directly. It only PROPOSES work by key; the Runner validates and a
/// sandbox worker performs the deterministic rewrite + build/test. The result is
/// always a reviewable pull request — never an auto-merge.
/// </summary>
public sealed class DotNetUpgradeService
{
    private readonly DotNetUpgraderOptions _options;
    private readonly IDotNetUpgradeTrigger _trigger;

    public DotNetUpgradeService(IOptions<DotNetUpgraderOptions> options, IDotNetUpgradeTrigger trigger)
    {
        _options = options.Value;
        _trigger = trigger;
    }

    /// <summary>Plan one upgrade candidate per watched repository.</summary>
    public IReadOnlyList<DotNetUpgradeCandidate> PlanUpgrades() =>
        _options.RepositoryKeys
            .Select(key => new DotNetUpgradeCandidate(key, _options.TargetFramework))
            .ToList();

    /// <summary>
    /// Start a platform workflow for a single upgrade candidate. The repository
    /// key must be on the agent's watch list; the Runner performs the
    /// authoritative allowlist validation afterwards.
    /// </summary>
    public async Task<AgentJobResult> StartUpgradeWorkflowAsync(
        DotNetUpgradeCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        if (!_options.RepositoryKeys.Contains(candidate.RepositoryKey, StringComparer.OrdinalIgnoreCase))
        {
            return AgentJobResult.Rejected(
                Guid.NewGuid().ToString("N"),
                $"Repository '{candidate.RepositoryKey}' is not watched by DotNetUpgrader.");
        }

        var request = new DotNetUpgradeJobRequest
        {
            RepositoryKey = candidate.RepositoryKey,
            TargetFramework = candidate.TargetFramework,
            OnlyUpgrade = _options.OnlyUpgrade,
            RequestedBy = "DotNetUpgrader",
        };

        return await _trigger.StartDotNetUpgradeAsync(request, cancellationToken);
    }
}
