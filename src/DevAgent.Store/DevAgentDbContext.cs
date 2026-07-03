namespace DevAgent.Store;

using Microsoft.EntityFrameworkCore;

/// <summary>EF Core context for the platform's mutable configuration store.</summary>
public class DevAgentDbContext : DbContext
{
    public DevAgentDbContext(DbContextOptions<DevAgentDbContext> options) : base(options) { }

    public DbSet<RepositoryEntity> Repositories => Set<RepositoryEntity>();
    public DbSet<PackageEntity> Packages => Set<PackageEntity>();
    public DbSet<ContainerImageEntity> ContainerImages => Set<ContainerImageEntity>();
    public DbSet<JobTypeImageEntity> JobTypeImages => Set<JobTypeImageEntity>();
    public DbSet<TargetFrameworkEntity> TargetFrameworks => Set<TargetFrameworkEntity>();
    public DbSet<PackageUsageEntity> PackageUsages => Set<PackageUsageEntity>();
    public DbSet<McpServerEntity> McpServers => Set<McpServerEntity>();
    public DbSet<SkillEntity> Skills => Set<SkillEntity>();
    public DbSet<WebhookEntity> Webhooks => Set<WebhookEntity>();
    public DbSet<AgentSettingEntity> AgentSettings => Set<AgentSettingEntity>();
    public DbSet<AdminUserEntity> AdminUsers => Set<AdminUserEntity>();
    public DbSet<ConfigChangeEntity> ConfigChanges => Set<ConfigChangeEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PackageUsageEntity>()
            .HasKey(u => new { u.RepositoryKey, u.PackageId });
    }
}
