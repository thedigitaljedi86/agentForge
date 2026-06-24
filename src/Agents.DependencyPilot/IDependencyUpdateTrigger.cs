namespace Agents.DependencyPilot;

using DevAgent.Contracts.Jobs;

/// <summary>
/// Seam through which DependencyPilot starts a platform workflow. In production
/// this is backed by a Hub client; in tests it is a fake. Keeping the agent
/// decoupled from the Hub's transport keeps it testable and free of platform
/// infrastructure code.
/// </summary>
public interface IDependencyUpdateTrigger
{
    Task<AgentJobResult> StartDependencyUpdateAsync(
        NuGetUpdateJobRequest request,
        CancellationToken cancellationToken = default);
}
