namespace Agents.CodeReviewer;

using DevAgent.Contracts.Jobs;

/// <summary>
/// Seam through which CodeReviewer starts a review workflow. Backed by a Hub
/// client in production, a fake in tests.
/// </summary>
public interface ICodeReviewTrigger
{
    Task<AgentJobResult> StartCodeReviewAsync(
        CodeReviewJobRequest request,
        CancellationToken cancellationToken = default);
}
