namespace DevAgent.Store;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registration + first-run seeding for the configuration store.
///
/// Seeding preserves current behaviour: on an empty database, the existing
/// appsettings sections (Guard, DependencyPilot, DotNetUpgrader, PackageUsage)
/// are imported once. From then on, the DATABASE is the source of truth and the
/// admin UI is how it changes — every change audited.
/// </summary>
public static class StoreSetup
{
    public const string ConnectionStringName = "DevAgent";

    /// <summary>Registers the DbContext when a connection string is configured. Returns true when enabled.</summary>
    public static bool TryAddDevAgentStore(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return false;
        }

        services.AddDbContextFactory<DevAgentDbContext>(o => o.UseSqlite(connectionString));
        return true;
    }

    /// <summary>Creates the schema (idempotent), applies WAL, and seeds an empty database from configuration.</summary>
    public static async Task InitializeAsync(DevAgentDbContext db, IConfiguration configuration, CancellationToken ct = default)
    {
        await db.Database.EnsureCreatedAsync(ct);

        // WAL makes the Hub-writes / Runner-reads sharing pattern safe on a volume.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);

        await SeedGuardAsync(db, configuration, ct);
        await SeedAgentsAsync(db, configuration, ct);
        await SeedWebhooksAsync(db, ct);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            // Hub and Runner both seed on startup against the shared SQLite file
            // (see docker-compose.yml). The loser of that race lands here after
            // the winner already committed the same seed rows - nothing to do.
        }
    }

    private static async Task SeedGuardAsync(DevAgentDbContext db, IConfiguration configuration, CancellationToken ct)
    {
        var guard = configuration.GetSection("Guard");

        if (!await db.Repositories.AnyAsync(ct))
        {
            foreach (var repo in guard.GetSection("Repositories").GetChildren())
            {
                var key = repo["Key"];
                if (!string.IsNullOrWhiteSpace(key))
                {
                    db.Repositories.Add(new RepositoryEntity
                    {
                        Key = key!,
                        CloneUrl = repo["CloneUrl"] ?? string.Empty,
                        BaseBranch = repo["BaseBranch"] ?? "main",
                    });
                }
            }
        }

        if (!await db.Packages.AnyAsync(ct))
        {
            foreach (var id in guard.GetSection("Packages").Get<string[]>() ?? Array.Empty<string>())
            {
                db.Packages.Add(new PackageEntity { PackageId = id });
            }
        }

        if (!await db.ContainerImages.AnyAsync(ct))
        {
            foreach (var image in guard.GetSection("ContainerImages").Get<string[]>() ?? Array.Empty<string>())
            {
                db.ContainerImages.Add(new ContainerImageEntity { Image = image });
            }
        }

        // Per-key add-if-missing so a database created before a job type
        // existed still receives its mapping on upgrade (admins can edit it).
        var knownJobTypes = await db.JobTypeImages.Select(j => j.JobType).ToListAsync(ct);
        foreach (var pair in guard.GetSection("JobTypeImages").GetChildren())
        {
            if (!knownJobTypes.Contains(pair.Key))
            {
                db.JobTypeImages.Add(new JobTypeImageEntity { JobType = pair.Key, Image = pair.Value ?? string.Empty });
            }
        }

        if (!await db.TargetFrameworks.AnyAsync(ct))
        {
            foreach (var tfm in guard.GetSection("AllowedTargetFrameworks").Get<string[]>() ?? Array.Empty<string>())
            {
                db.TargetFrameworks.Add(new TargetFrameworkEntity { Framework = tfm });
            }
        }

        if (!await db.PackageUsages.AnyAsync(ct))
        {
            foreach (var repo in configuration.GetSection("PackageUsage:Repositories").GetChildren())
            {
                foreach (var usage in repo.GetChildren())
                {
                    var packageId = usage["PackageId"];
                    if (!string.IsNullOrWhiteSpace(packageId))
                    {
                        db.PackageUsages.Add(new PackageUsageEntity
                        {
                            RepositoryKey = repo.Key,
                            PackageId = packageId!,
                            CurrentVersion = usage["CurrentVersion"],
                        });
                    }
                }
            }
        }
    }

    private static async Task SeedAgentsAsync(DevAgentDbContext db, IConfiguration configuration, CancellationToken ct)
    {
        // Per-agent add-if-missing, so databases created before an agent
        // existed still receive its settings row on upgrade.
        var existing = await db.AgentSettings.Select(a => a.AgentName).ToListAsync(ct);

        if (!existing.Contains("DependencyPilot"))
        {
            var pilot = configuration.GetSection("DependencyPilot");
            db.AgentSettings.Add(new AgentSettingEntity
            {
                AgentName = "DependencyPilot",
                RepositoryKeysJson = ToJsonArray(pilot.GetSection("RepositoryKeys").Get<string[]>()),
                WatchedPackagesJson = ToJsonArray(pilot.GetSection("WatchedPackages").Get<string[]>()),
                IncludePrerelease = pilot.GetValue("IncludePrerelease", false),
                LlmProvider = pilot["Llm:Provider"],
                LlmModel = pilot["Llm:Model"],
            });
        }

        if (!existing.Contains("DotNetUpgrader"))
        {
            var upgrader = configuration.GetSection("DotNetUpgrader");
            db.AgentSettings.Add(new AgentSettingEntity
            {
                AgentName = "DotNetUpgrader",
                RepositoryKeysJson = ToJsonArray(upgrader.GetSection("RepositoryKeys").Get<string[]>()),
                TargetFramework = upgrader["TargetFramework"],
                LlmProvider = upgrader["Llm:Provider"],
                LlmModel = upgrader["Llm:Model"],
            });
        }

        foreach (var agentName in new[] { "PipelineDoctor", "DocScribe", "CodeReviewer" })
        {
            if (!existing.Contains(agentName))
            {
                var section = configuration.GetSection(agentName);
                db.AgentSettings.Add(new AgentSettingEntity
                {
                    AgentName = agentName,
                    RepositoryKeysJson = ToJsonArray(section.GetSection("RepositoryKeys").Get<string[]>()),
                    LlmProvider = section["Llm:Provider"],
                    LlmModel = section["Llm:Model"],
                });
            }
        }
    }

    private static async Task SeedWebhooksAsync(DevAgentDbContext db, CancellationToken ct)
    {
        var existing = await db.Webhooks.Select(w => w.Key).ToListAsync(ct);
        foreach (var key in new[] { "nuget-package-published", "pull-request-opened" })
        {
            if (!existing.Contains(key))
            {
                db.Webhooks.Add(new WebhookEntity { Key = key, Enabled = true });
            }
        }
    }

    private static string ToJsonArray(string[]? values) =>
        System.Text.Json.JsonSerializer.Serialize(values ?? Array.Empty<string>());
}
