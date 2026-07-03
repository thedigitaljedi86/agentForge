namespace DevAgent.Runner.Tests;

using DevAgent.Audit;
using DevAgent.Bridge.Mcp;
using DevAgent.Contracts.Jobs;
using DevAgent.Contracts.Sandbox;
using DevAgent.Runner.Api.Mcp;
using DevAgent.Store;
using Microsoft.EntityFrameworkCore;
using Xunit;

public class McpJobTokenStoreTests
{
    [Fact]
    public void Issued_token_resolves_to_its_job_and_grants()
    {
        var store = new InMemoryMcpJobTokenStore();
        var grants = new[] { new McpGrant { ServerKey = "adv", Tools = new[] { "query" } } };

        var token = store.Issue("job-1", grants);
        var access = store.Resolve(token);

        Assert.NotNull(access);
        Assert.Equal("job-1", access!.JobId);
        Assert.Equal("adv", access.Grants.Single().ServerKey);
    }

    [Fact]
    public void Unknown_expired_or_revoked_tokens_fail_closed()
    {
        var store = new InMemoryMcpJobTokenStore(lifetime: TimeSpan.FromMilliseconds(-1));
        var expired = store.Issue("job-1", Array.Empty<McpGrant>());
        Assert.Null(store.Resolve(expired));

        var live = new InMemoryMcpJobTokenStore();
        var token = live.Issue("job-2", Array.Empty<McpGrant>());
        live.Revoke("job-2");
        Assert.Null(live.Resolve(token));

        Assert.Null(live.Resolve("garbage"));
        Assert.Null(live.Resolve(""));
    }

    [Fact]
    public void Tokens_are_unique_and_unguessable_length()
    {
        var store = new InMemoryMcpJobTokenStore();
        var a = store.Issue("j", Array.Empty<McpGrant>());
        var b = store.Issue("j", Array.Empty<McpGrant>());

        Assert.NotEqual(a, b);
        Assert.True(Convert.FromBase64String(a).Length >= 32); // 256-bit
    }
}

public class SandboxJobEnricherTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"devagent-enrich-{Guid.NewGuid():N}.db");
    private TestDbFactory _factory = null!;

    private sealed class TestDbFactory : IDbContextFactory<DevAgentDbContext>
    {
        private readonly DbContextOptions<DevAgentDbContext> _options;
        public TestDbFactory(string path) =>
            _options = new DbContextOptionsBuilder<DevAgentDbContext>().UseSqlite($"Data Source={path}").Options;
        public DevAgentDbContext CreateDbContext() => new(_options);
    }

    private sealed class FakeMcp : IMcpClient
    {
        public Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(string serverKey, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<McpToolDescriptor>>(new[]
            {
                new McpToolDescriptor { ServerKey = serverKey, Name = "query", Description = "Query advisories" },
                new McpToolDescriptor { ServerKey = serverKey, Name = "publish", Description = "not granted" },
            });

        public Task<McpCallResult> CallToolAsync(string serverKey, string tool, string argumentsJson, CancellationToken ct = default)
            => Task.FromResult(new McpCallResult { Success = true, Content = "ok" });

        public Task<IReadOnlyList<McpPromptDescriptor>> ListPromptsAsync(string serverKey, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<McpPromptDescriptor>>(Array.Empty<McpPromptDescriptor>());

        public Task<McpPromptResult> GetPromptAsync(string serverKey, string promptName, string argumentsJson, CancellationToken ct = default)
            => Task.FromResult(new McpPromptResult { Text = $"PROMPT[{serverKey}/{promptName}]" });
    }

    public async Task InitializeAsync()
    {
        _factory = new TestDbFactory(_dbPath);
        await using var db = _factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();

        db.McpServers.Add(new McpServerEntity
        {
            Key = "advisories",
            Endpoint = "https://mcp.internal/adv",
            AllowedToolsJson = JsonColumns.FromList(new[] { "query" }), // registry allows only query
            AllowedPromptsJson = JsonColumns.FromList(new[] { "pr-style" }),
        });
        db.AgentSettings.Add(new AgentSettingEntity
        {
            AgentName = "DependencyPilot",
            LlmProvider = "Claude",
            LlmModel = "claude-opus-4-8",
            McpGrantsJson = """[{"serverKey":"advisories","tools":["query","publish"],"prompts":["pr-style"]}]""",
            SkillKeysJson = JsonColumns.FromList(new[] { "advisory-check", "needs-ungranted", "mcp-prompt-skill" }),
        });
        db.Skills.AddRange(
            new SkillEntity
            {
                Key = "advisory-check",
                Name = "Advisory check",
                Instructions = "Query advisories before changing versions.",
                RequiredToolsJson = JsonColumns.FromList(new[] { "read_file", "mcp__advisories__query" }),
            },
            new SkillEntity
            {
                Key = "needs-ungranted",
                Name = "Should be refused",
                Instructions = "This skill must NOT be applied.",
                RequiredToolsJson = JsonColumns.FromList(new[] { "mcp__advisories__publish" }), // not in registry allowlist
            },
            new SkillEntity
            {
                Key = "mcp-prompt-skill",
                Name = "PR style",
                McpServerKey = "advisories",
                McpPromptName = "pr-style",
            });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        try { File.Delete(_dbPath); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    private static SandboxJobRequest Request() => new()
    {
        JobId = "job-1",
        JobType = AgentJobType.NuGetUpdate,
        CloneUrl = "https://git/x.git",
        BaseBranch = "main",
        ContainerImage = "registry/worker:8.0",
    };

    private (StoreSandboxJobEnricher enricher, InMemoryMcpJobTokenStore tokens) NewEnricher()
    {
        var tokens = new InMemoryMcpJobTokenStore();
        return (new StoreSandboxJobEnricher(_factory, tokens, new FakeMcp(), new ConsoleAuditLog()), tokens);
    }

    [Fact]
    public async Task Known_agent_gets_llm_pin_granted_tools_and_a_token()
    {
        var (enricher, tokens) = NewEnricher();

        var enriched = await enricher.EnrichAsync(Request(), "DependencyPilot");

        Assert.Equal("Claude", enriched.LlmProvider);
        Assert.Contains("query", enriched.McpToolsJson);
        // "publish" was granted by the agent but NOT allowlisted in the
        // registry — the intersection excludes it.
        Assert.DoesNotContain("publish", enriched.McpToolsJson);

        Assert.NotNull(enriched.McpGatewayToken);
        var access = tokens.Resolve(enriched.McpGatewayToken!);
        Assert.Equal("job-1", access!.JobId);
    }

    [Fact]
    public async Task Skills_are_applied_refused_or_prompt_backed_correctly()
    {
        var (enricher, _) = NewEnricher();

        var enriched = await enricher.EnrichAsync(Request(), "DependencyPilot");

        Assert.Contains("Query advisories before changing versions.", enriched.SkillInstructions);
        Assert.Contains("PROMPT[advisories/pr-style]", enriched.SkillInstructions); // MCP-prompt-backed
        Assert.DoesNotContain("must NOT be applied", enriched.SkillInstructions);   // requires ungranted tool
    }

    [Fact]
    public async Task Unknown_requester_gets_no_capabilities()
    {
        var (enricher, _) = NewEnricher();

        var enriched = await enricher.EnrichAsync(Request(), "manual");

        Assert.Null(enriched.LlmProvider);
        Assert.Equal("[]", enriched.McpToolsJson);
        Assert.Null(enriched.McpGatewayToken);
        Assert.Null(enriched.SkillInstructions);
    }
}

public class CliSandboxMcpEnvTests
{
    private const string Image = "registry/worker:8.0";

    private sealed class RecordingLauncher : DevAgent.Runner.Api.Sandbox.ISandboxProcessLauncher
    {
        public IReadOnlyList<string>? LastArgs { get; private set; }

        public Task<DevAgent.Runner.Api.Sandbox.SandboxProcessResult> LaunchAsync(
            string cli, IReadOnlyList<string> arguments, CancellationToken ct)
        {
            LastArgs = arguments;
            return Task.FromResult(new DevAgent.Runner.Api.Sandbox.SandboxProcessResult(0, "", ""));
        }
    }

    [Fact]
    public async Task Gateway_token_and_tools_reach_the_worker_only_when_gateway_is_configured()
    {
        var launcher = new RecordingLauncher();
        var runner = new DevAgent.Runner.Api.Sandbox.CliSandboxJobRunner(
            new DevAgent.Guard.Policies.ContainerImagePolicy(new[] { Image }),
            new DevAgent.Runner.Api.Sandbox.SandboxOptions
            {
                WorkerGitToken = "t",
                McpGatewayBaseUrl = "http://runner:8080",
            },
            launcher,
            new ConsoleAuditLog());

        var request = new SandboxJobRequest
        {
            JobId = "j",
            JobType = AgentJobType.NuGetUpdate,
            CloneUrl = "https://git/x.git",
            BaseBranch = "main",
            ContainerImage = Image,
            LlmProvider = "Claude",
            McpGatewayToken = "job-token",
            McpToolsJson = """[{"serverKey":"adv","name":"query"}]""",
            SkillInstructions = "Check advisories first.",
        };

        await runner.RunAsync(request);

        var args = launcher.LastArgs!;
        Assert.Contains(args, a => a == "DEVAGENT_MCP_GATEWAY=http://runner:8080");
        Assert.Contains(args, a => a == "DEVAGENT_MCP_TOKEN=job-token");
        Assert.Contains(args, a => a.StartsWith("DEVAGENT_MCP_TOOLS=", StringComparison.Ordinal));
        Assert.Contains(args, a => a == "DEVAGENT_LLM_PROVIDER=Claude"); // agent pin wins
        Assert.Contains(args, a => a.StartsWith("DEVAGENT_SKILL_INSTRUCTIONS=", StringComparison.Ordinal));

        // No MCP server endpoint or credential ever reaches the sandbox.
        Assert.DoesNotContain(args, a => a.Contains("mcp.internal"));
    }

    [Fact]
    public async Task No_gateway_configured_means_no_mcp_env_at_all()
    {
        var launcher = new RecordingLauncher();
        var runner = new DevAgent.Runner.Api.Sandbox.CliSandboxJobRunner(
            new DevAgent.Guard.Policies.ContainerImagePolicy(new[] { Image }),
            new DevAgent.Runner.Api.Sandbox.SandboxOptions { WorkerGitToken = "t" }, // no gateway url
            launcher,
            new ConsoleAuditLog());

        await runner.RunAsync(new SandboxJobRequest
        {
            JobId = "j",
            JobType = AgentJobType.NuGetUpdate,
            CloneUrl = "https://git/x.git",
            BaseBranch = "main",
            ContainerImage = Image,
            McpGatewayToken = "job-token", // token minted but gateway not exposed
        });

        Assert.DoesNotContain(launcher.LastArgs!, a => a.StartsWith("DEVAGENT_MCP_", StringComparison.Ordinal));
    }
}
