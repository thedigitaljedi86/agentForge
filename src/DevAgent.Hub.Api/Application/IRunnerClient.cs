namespace DevAgent.Hub.Api.Application;

using DevAgent.Contracts.Jobs;

/// <summary>
/// Abstraction over the HTTP call from Hub to Runner. Keeping it behind an
/// interface keeps the application service testable and the transport swappable.
/// </summary>
public interface IRunnerClient
{
    Task<AgentJobResult> StartNuGetUpdateAsync(
        NuGetUpdateJobRequest request,
        CancellationToken cancellationToken = default);

    Task<AgentJobResult> StartDotNetUpgradeAsync(
        DotNetUpgradeJobRequest request,
        CancellationToken cancellationToken = default);

    Task<AgentJobResult> StartPipelineFixAsync(
        PipelineFixJobRequest request,
        CancellationToken cancellationToken = default);

    Task<AgentJobResult> StartDocUpdateAsync(
        DocUpdateJobRequest request,
        CancellationToken cancellationToken = default);

    Task<AgentJobResult> StartCodeReviewAsync(
        CodeReviewJobRequest request,
        CancellationToken cancellationToken = default);
}
