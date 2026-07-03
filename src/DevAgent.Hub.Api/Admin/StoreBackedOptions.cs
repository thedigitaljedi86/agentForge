namespace DevAgent.Hub.Api.Admin;

using Agents.CodeReviewer;
using Agents.DependencyPilot;
using Agents.DocScribe;
using Agents.DotNetUpgrader;
using Agents.PipelineDoctor;
using DevAgent.Bridge.Llm;
using DevAgent.Bridge.NuGet;
using DevAgent.Store;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

/// <summary>
/// Bridges the admin store to the agents' options types, so an admin-console
/// edit takes effect on the agents' NEXT run without a restart. Registered
/// only when the store is enabled; otherwise appsettings binding applies.
/// </summary>
public static class StoreBackedOptions
{
    public static void AddStoreBackedAgentOptions(this IServiceCollection services)
    {
        // Scoped: read fresh per request / per Hangfire job execution.
        services.AddScoped<IOptions<DependencyPilotOptions>>(sp =>
        {
            var db = sp.GetRequiredService<IDbContextFactory<DevAgentDbContext>>().CreateDbContext();
            using (db)
            {
                var row = db.AgentSettings.AsNoTracking().FirstOrDefault(a => a.AgentName == "DependencyPilot");
                var options = new DependencyPilotOptions();
                if (row is not null)
                {
                    options.RepositoryKeys = JsonColumns.ToList(row.RepositoryKeysJson).ToList();
                    options.WatchedPackages = JsonColumns.ToList(row.WatchedPackagesJson).ToList();
                    options.IncludePrerelease = row.IncludePrerelease;
                    ApplyLlm(options.Llm, row);
                }

                return Options.Create(options);
            }
        });

        services.AddScoped<IOptions<DotNetUpgraderOptions>>(sp =>
        {
            var db = sp.GetRequiredService<IDbContextFactory<DevAgentDbContext>>().CreateDbContext();
            using (db)
            {
                var row = db.AgentSettings.AsNoTracking().FirstOrDefault(a => a.AgentName == "DotNetUpgrader");
                var options = new DotNetUpgraderOptions();
                if (row is not null)
                {
                    options.RepositoryKeys = JsonColumns.ToList(row.RepositoryKeysJson).ToList();
                    options.TargetFramework = row.TargetFramework ?? options.TargetFramework;
                    ApplyLlm(options.Llm, row);
                }

                return Options.Create(options);
            }
        });

        services.AddScoped<IOptions<PipelineDoctorOptions>>(sp =>
        {
            var db = sp.GetRequiredService<IDbContextFactory<DevAgentDbContext>>().CreateDbContext();
            using (db)
            {
                var row = db.AgentSettings.AsNoTracking().FirstOrDefault(a => a.AgentName == "PipelineDoctor");
                var options = new PipelineDoctorOptions();
                if (row is not null)
                {
                    options.RepositoryKeys = JsonColumns.ToList(row.RepositoryKeysJson).ToList();
                    ApplyLlm(options.Llm, row);
                }

                return Options.Create(options);
            }
        });

        services.AddScoped<IOptions<DocScribeOptions>>(sp =>
        {
            var db = sp.GetRequiredService<IDbContextFactory<DevAgentDbContext>>().CreateDbContext();
            using (db)
            {
                var row = db.AgentSettings.AsNoTracking().FirstOrDefault(a => a.AgentName == "DocScribe");
                var options = new DocScribeOptions();
                if (row is not null)
                {
                    options.RepositoryKeys = JsonColumns.ToList(row.RepositoryKeysJson).ToList();
                    ApplyLlm(options.Llm, row);
                }

                return Options.Create(options);
            }
        });

        services.AddScoped<IOptions<CodeReviewerOptions>>(sp =>
        {
            var db = sp.GetRequiredService<IDbContextFactory<DevAgentDbContext>>().CreateDbContext();
            using (db)
            {
                var row = db.AgentSettings.AsNoTracking().FirstOrDefault(a => a.AgentName == "CodeReviewer");
                var options = new CodeReviewerOptions();
                if (row is not null)
                {
                    options.RepositoryKeys = JsonColumns.ToList(row.RepositoryKeysJson).ToList();
                    ApplyLlm(options.Llm, row);
                }

                return Options.Create(options);
            }
        });

        // The usage scanner reads the store's usage map fresh per scope.
        services.AddScoped<IPackageUsageScanner>(sp =>
        {
            var db = sp.GetRequiredService<IDbContextFactory<DevAgentDbContext>>().CreateDbContext();
            using (db)
            {
                var map = new PackageUsageMapOptions();
                foreach (var group in db.PackageUsages.AsNoTracking().AsEnumerable().GroupBy(u => u.RepositoryKey))
                {
                    map.Repositories[group.Key] = group
                        .Select(u => new ConfiguredPackageUsage { PackageId = u.PackageId, CurrentVersion = u.CurrentVersion })
                        .ToList();
                }

                return new ConfiguredPackageUsageScanner(map);
            }
        });
    }

    private static void ApplyLlm(LlmClientOptions llm, AgentSettingEntity row)
    {
        if (Enum.TryParse<LlmProvider>(row.LlmProvider, ignoreCase: true, out var provider))
        {
            llm.Provider = provider;
        }

        llm.Model = row.LlmModel;
    }
}
