namespace Agents.DotNetUpgrader;

using DevAgent.Contracts.Jobs;

/// <summary>
/// Seam through which DotNetUpgrader starts a platform workflow. In production
/// this is backed by a Hub client; in tests it is a fake. Keeping the agent
/// decoupled from the Hub's transport keeps it testable and free of platform
/// infrastructure code.
/// </summary>
public interface IDotNetUpgradeTrigger
{
    Task<AgentJobResult> StartDotNetUpgradeAsync(
        DotNetUpgradeJobRequest request,
        CancellationToken cancellationToken = default);
}
