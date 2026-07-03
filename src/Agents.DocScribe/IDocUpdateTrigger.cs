namespace Agents.DocScribe;

using DevAgent.Contracts.Jobs;

/// <summary>
/// Seam through which DocScribe starts a documentation workflow. Backed by a
/// Hub client in production, a fake in tests.
/// </summary>
public interface IDocUpdateTrigger
{
    Task<AgentJobResult> StartDocUpdateAsync(
        DocUpdateJobRequest request,
        CancellationToken cancellationToken = default);
}
