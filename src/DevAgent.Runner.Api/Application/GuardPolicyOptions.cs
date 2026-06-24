namespace DevAgent.Runner.Api.Application;

using DevAgent.Contracts.Jobs;
using DevAgent.Guard.Policies;

/// <summary>
/// Bindable options describing every allowlist, sourced from configuration
/// (appsettings / environment). Administrators edit these; callers cannot.
/// </summary>
public sealed class GuardPolicyOptions
{
    public const string SectionName = "Guard";

    public List<RepositoryOption> Repositories { get; set; } = new();
    public List<string> Packages { get; set; } = new();
    public List<string> ContainerImages { get; set; } = new();

    /// <summary>Map of job type -> container image used for that job type.</summary>
    public Dictionary<string, string> JobTypeImages { get; set; } = new();

    public sealed class RepositoryOption
    {
        public string Key { get; set; } = string.Empty;
        public string CloneUrl { get; set; } = string.Empty;
        public string BaseBranch { get; set; } = "main";
    }

    /// <summary>Builds the immutable <see cref="GuardPolicySet"/> from options.</summary>
    public GuardPolicySet Build()
    {
        var repositories = new RepositoryPolicy(Repositories.Select(r => new RepositoryEntry
        {
            Key = r.Key,
            CloneUrl = r.CloneUrl,
            BaseBranch = r.BaseBranch,
        }));

        var jobTypeImages = JobTypeImages.ToDictionary(
            kv => Enum.Parse<AgentJobType>(kv.Key, ignoreCase: true),
            kv => kv.Value);

        return new GuardPolicySet
        {
            Repositories = repositories,
            Packages = new PackagePolicy(Packages),
            ContainerImages = new ContainerImagePolicy(ContainerImages),
            JobTypes = new JobPolicy(jobTypeImages),
        };
    }
}
