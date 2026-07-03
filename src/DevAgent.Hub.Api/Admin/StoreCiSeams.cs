namespace DevAgent.Hub.Api.Admin;

using DevAgent.Bridge.Ci;
using DevAgent.Store;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Store-backed CI connection lookup for PipelineDoctor. The admin console
/// manages the rows; the token remains an env-var NAME resolved on this host
/// by the CI bridge — never a stored value.
/// </summary>
public sealed class StoreCiConnectionSource : ICiConnectionSource
{
    private readonly IDbContextFactory<DevAgentDbContext> _dbFactory;

    public StoreCiConnectionSource(IDbContextFactory<DevAgentDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<CiConnection?> GetAsync(string repositoryKey, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.CiConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.RepositoryKey == repositoryKey, ct);
        if (row is null || !Enum.TryParse<CiProviderKind>(row.Provider, ignoreCase: true, out var kind))
        {
            return null;
        }

        return new CiConnection
        {
            RepositoryKey = row.RepositoryKey,
            Provider = kind,
            BaseUrl = row.BaseUrl,
            ProjectPath = row.ProjectPath,
            TokenEnvVar = row.TokenEnvVar,
        };
    }
}

/// <summary>Store-backed dedupe of already-handled failed pipeline runs.</summary>
public sealed class StoreProcessedRunStore : IProcessedRunStore
{
    private readonly IDbContextFactory<DevAgentDbContext> _dbFactory;

    public StoreProcessedRunStore(IDbContextFactory<DevAgentDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<bool> IsProcessedAsync(string repositoryKey, string runId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.ProcessedPipelineRuns.AsNoTracking()
            .AnyAsync(r => r.RepositoryKey == repositoryKey && r.RunId == runId, ct);
    }

    public async Task MarkProcessedAsync(string repositoryKey, string runId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        if (!await db.ProcessedPipelineRuns.AnyAsync(r => r.RepositoryKey == repositoryKey && r.RunId == runId, ct))
        {
            db.ProcessedPipelineRuns.Add(new ProcessedPipelineRunEntity
            {
                RepositoryKey = repositoryKey,
                RunId = runId,
            });
            await db.SaveChangesAsync(ct);
        }
    }
}

/// <summary>No-store fallbacks: no CI connections; in-memory dedupe.</summary>
public sealed class NullCiConnectionSource : ICiConnectionSource
{
    public Task<CiConnection?> GetAsync(string repositoryKey, CancellationToken ct = default) =>
        Task.FromResult<CiConnection?>(null);
}

/// <summary>In-memory processed-run store (config-only mode; resets on restart).</summary>
public sealed class InMemoryProcessedRunStore : IProcessedRunStore
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public Task<bool> IsProcessedAsync(string repositoryKey, string runId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_seen.Contains($"{repositoryKey}\n{runId}"));
        }
    }

    public Task MarkProcessedAsync(string repositoryKey, string runId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _seen.Add($"{repositoryKey}\n{runId}");
        }

        return Task.CompletedTask;
    }
}
