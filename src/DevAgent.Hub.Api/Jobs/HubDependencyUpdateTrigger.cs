namespace DevAgent.Hub.Api.Jobs;

using Agents.DependencyPilot;
using DevAgent.Contracts.Jobs;
using DevAgent.Hub.Api.Application;

/// <summary>
/// Connects the DependencyPilot agent to the platform workflow: when the agent
/// proposes an update, this trigger routes it through the Hub application
/// service (audit + job tracker) and on to the Runner's validation gate. The
/// agent itself never talks to the Runner or a sandbox directly.
/// </summary>
public sealed class HubDependencyUpdateTrigger : IDependencyUpdateTrigger
{
    private readonly HubJobApplicationService _service;

    public HubDependencyUpdateTrigger(HubJobApplicationService service)
    {
        _service = service;
    }

    public Task<AgentJobResult> StartDependencyUpdateAsync(
        NuGetUpdateJobRequest request,
        CancellationToken cancellationToken = default)
        => _service.StartNuGetUpdateAsync(request, cancellationToken);
}
