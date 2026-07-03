namespace DevAgent.Runner.Api.Application;

using DevAgent.Guard.Policies;
using DevAgent.Store;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Supplies the authoritative <see cref="GuardPolicySet"/> per validation.
/// Two implementations: configuration-file (immutable, the original behaviour)
/// and store-backed (SQLite — what the admin UI edits). Rebuilding per request
/// means an admin change takes effect on the NEXT job with no restart, and the
/// Runner remains the single authority either way.
/// </summary>
public interface IGuardPolicySource
{
    ValueTask<GuardPolicySet> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>Original behaviour: policies come from appsettings, fixed at startup.</summary>
public sealed class ConfigGuardPolicySource : IGuardPolicySource
{
    private readonly GuardPolicySet _set;

    public ConfigGuardPolicySource(GuardPolicyOptions options) => _set = options.Build();

    public ValueTask<GuardPolicySet> GetAsync(CancellationToken cancellationToken = default) => new(_set);
}

/// <summary>
/// Store-backed policies: read fresh from SQLite on every validation. The
/// tables are tiny, SQLite reads are microseconds, and freshness beats caching
/// for a security gate.
/// </summary>
public sealed class StoreGuardPolicySource : IGuardPolicySource
{
    private readonly IDbContextFactory<DevAgentDbContext> _dbFactory;

    public StoreGuardPolicySource(IDbContextFactory<DevAgentDbContext> dbFactory) => _dbFactory = dbFactory;

    public async ValueTask<GuardPolicySet> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var repositories = await db.Repositories.AsNoTracking().ToListAsync(cancellationToken);
        var packages = await db.Packages.AsNoTracking().Select(p => p.PackageId).ToListAsync(cancellationToken);
        var images = await db.ContainerImages.AsNoTracking().Select(i => i.Image).ToListAsync(cancellationToken);
        var jobTypeImages = await db.JobTypeImages.AsNoTracking().ToListAsync(cancellationToken);
        var frameworks = await db.TargetFrameworks.AsNoTracking().Select(f => f.Framework).ToListAsync(cancellationToken);

        var jobTypeMap = new Dictionary<Contracts.Jobs.AgentJobType, string>();
        foreach (var entry in jobTypeImages)
        {
            if (Enum.TryParse<Contracts.Jobs.AgentJobType>(entry.JobType, ignoreCase: true, out var jobType))
            {
                jobTypeMap[jobType] = entry.Image;
            }
        }

        return new GuardPolicySet
        {
            Repositories = new RepositoryPolicy(repositories.Select(r => new RepositoryEntry
            {
                Key = r.Key,
                CloneUrl = r.CloneUrl,
                BaseBranch = r.BaseBranch,
            })),
            Packages = new PackagePolicy(packages),
            ContainerImages = new ContainerImagePolicy(images),
            JobTypes = new JobPolicy(jobTypeMap),
            TargetFrameworks = new TargetFrameworkPolicy(frameworks),
        };
    }
}
