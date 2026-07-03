namespace DevAgent.Store.Tests;

using System.Text;
using DevAgent.Store;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

public class StoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"devagent-test-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }

    private DevAgentDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DevAgentDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        return new DevAgentDbContext(options);
    }

    private static IConfiguration ConfigFrom(string json) =>
        new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();

    private const string SampleConfig = """
    {
      "Guard": {
        "Repositories": [ { "Key": "svc-a", "CloneUrl": "https://git/svc-a.git", "BaseBranch": "main" } ],
        "Packages": [ "Serilog" ],
        "ContainerImages": [ "registry/worker:8.0" ],
        "JobTypeImages": { "NuGetUpdate": "registry/worker:8.0" },
        "AllowedTargetFrameworks": [ "net10.0" ]
      },
      "PackageUsage": { "Repositories": { "svc-a": [ { "PackageId": "Serilog", "CurrentVersion": "2.0.0" } ] } },
      "DependencyPilot": { "RepositoryKeys": [ "svc-a" ], "WatchedPackages": [ "Serilog" ] },
      "DotNetUpgrader": { "RepositoryKeys": [ "svc-a" ], "TargetFramework": "net10.0" }
    }
    """;

    [Fact]
    public async Task Empty_database_is_seeded_from_configuration_once()
    {
        await using (var db = NewContext())
        {
            await StoreSetup.InitializeAsync(db, ConfigFrom(SampleConfig));
        }

        await using (var db = NewContext())
        {
            Assert.Equal("https://git/svc-a.git", (await db.Repositories.SingleAsync()).CloneUrl);
            Assert.Equal("Serilog", (await db.Packages.SingleAsync()).PackageId);
            Assert.Equal("registry/worker:8.0", (await db.ContainerImages.SingleAsync()).Image);
            Assert.Equal("NuGetUpdate", (await db.JobTypeImages.SingleAsync()).JobType);
            Assert.Equal("net10.0", (await db.TargetFrameworks.SingleAsync()).Framework);
            Assert.Equal("2.0.0", (await db.PackageUsages.SingleAsync()).CurrentVersion);
            Assert.Equal(2, await db.AgentSettings.CountAsync());
            Assert.True((await db.Webhooks.SingleAsync()).Enabled);
        }
    }

    [Fact]
    public async Task Reseeding_does_not_overwrite_admin_edits()
    {
        await using (var db = NewContext())
        {
            await StoreSetup.InitializeAsync(db, ConfigFrom(SampleConfig));
            db.Packages.Add(new PackageEntity { PackageId = "Polly" });
            (await db.Repositories.SingleAsync()).CloneUrl = "https://git/moved.git";
            await db.SaveChangesAsync();
        }

        await using (var db = NewContext())
        {
            await StoreSetup.InitializeAsync(db, ConfigFrom(SampleConfig)); // restart

            Assert.Equal(2, await db.Packages.CountAsync());                    // edit kept
            Assert.Equal("https://git/moved.git", (await db.Repositories.SingleAsync()).CloneUrl);
        }
    }

    [Fact]
    public async Task Mcp_and_skill_round_trip()
    {
        await using (var db = NewContext())
        {
            await StoreSetup.InitializeAsync(db, ConfigFrom(SampleConfig));
            db.McpServers.Add(new McpServerEntity
            {
                Key = "advisories",
                Endpoint = "https://mcp.internal/adv",
                AllowedToolsJson = JsonColumns.FromList(new[] { "query" }),
                AllowedPromptsJson = JsonColumns.FromList(new[] { "summarize" }),
                AuthTokenEnvVar = "MCP_ADV_TOKEN",
            });
            db.Skills.Add(new SkillEntity
            {
                Key = "fix-with-advisories",
                Instructions = "Check advisories before changing versions.",
                RequiredToolsJson = JsonColumns.FromList(new[] { "mcp__advisories__query" }),
            });
            await db.SaveChangesAsync();
        }

        await using (var db = NewContext())
        {
            var server = await db.McpServers.SingleAsync();
            Assert.Equal(new[] { "query" }, JsonColumns.ToList(server.AllowedToolsJson));
            Assert.Equal("MCP_ADV_TOKEN", server.AuthTokenEnvVar); // reference, not a secret value

            var skill = await db.Skills.SingleAsync();
            Assert.Contains("mcp__advisories__query", JsonColumns.ToList(skill.RequiredToolsJson));
        }
    }
}

public class PasswordHasherTests
{
    [Fact]
    public void Correct_password_verifies_and_wrong_password_fails()
    {
        var (hash, salt, iterations) = PasswordHasher.Hash("hunter2!");

        Assert.True(PasswordHasher.Verify("hunter2!", hash, salt, iterations));
        Assert.False(PasswordHasher.Verify("hunter3!", hash, salt, iterations));
        Assert.False(PasswordHasher.Verify("", hash, salt, iterations));
    }

    [Fact]
    public void Hashes_are_salted_uniquely()
    {
        var a = PasswordHasher.Hash("same-password");
        var b = PasswordHasher.Hash("same-password");
        Assert.NotEqual(a.HashBase64, b.HashBase64);
    }
}
