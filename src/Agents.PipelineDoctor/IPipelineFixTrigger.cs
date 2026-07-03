namespace Agents.PipelineDoctor;

using DevAgent.Contracts.Jobs;

/// <summary>
/// Seam through which PipelineDoctor starts a repair workflow. Backed by a Hub
/// client in production, a fake in tests — the agent never talks to the Runner
/// or a sandbox directly.
/// </summary>
public interface IPipelineFixTrigger
{
    Task<AgentJobResult> StartPipelineFixAsync(
        PipelineFixJobRequest request,
        CancellationToken cancellationToken = default);
}
