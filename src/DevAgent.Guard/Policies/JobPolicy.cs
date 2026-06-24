namespace DevAgent.Guard.Policies;

using DevAgent.Contracts.Jobs;
using DevAgent.Contracts.Validation;

/// <summary>
/// Allowlist of job types the platform is permitted to run, plus the image
/// chosen for each. Even if a request arrives for a known enum value, it is
/// only runnable if an administrator enabled it here.
/// </summary>
public sealed class JobPolicy
{
    private readonly IReadOnlyDictionary<AgentJobType, string> _imageByType;

    public JobPolicy(IReadOnlyDictionary<AgentJobType, string> allowedJobTypeImages)
    {
        _imageByType = allowedJobTypeImages;
    }

    public bool IsAllowed(AgentJobType jobType) =>
        jobType != AgentJobType.Unknown && _imageByType.ContainsKey(jobType);

    public ValidationResult Validate(AgentJobType jobType) =>
        IsAllowed(jobType)
            ? ValidationResult.Success
            : ValidationResult.Fail($"Job type '{jobType}' is not on the allowlist.");

    /// <summary>
    /// Returns the allowlisted container image for a job type. Throws if the
    /// job type is not enabled, so callers cannot fall back to a default image.
    /// </summary>
    public string ResolveImage(AgentJobType jobType)
    {
        if (!_imageByType.TryGetValue(jobType, out var image))
        {
            throw new PolicyViolationException($"Job type '{jobType}' is not on the allowlist.");
        }

        return image;
    }
}
