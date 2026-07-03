namespace DevAgent.Hub.Tests;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Xunit;

/// <summary>
/// End-to-end tests of the admin console API through a real test host:
/// authentication, CSRF header, CRUD with audit, and webhook secrets.
/// </summary>
public sealed class AdminConsoleIntegrationTests : IClassFixture<AdminConsoleIntegrationTests.HubFactory>
{
    public sealed class HubFactory : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Combine(Path.GetTempPath(), $"devagent-hub-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:DevAgent", $"Data Source={DbPath}");
            builder.UseSetting("Admin:Password", "test-password-1!");
            builder.UseSetting("Admin:Username", "admin");
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { File.Delete(DbPath); } catch { /* best effort */ }
        }
    }

    private readonly HubFactory _factory;

    public AdminConsoleIntegrationTests(HubFactory factory) => _factory = factory;

    private HttpClient NewClient() =>
        _factory.CreateDefaultClient(new CookieContainerHandler());

    private static async Task LoginAsync(HttpClient client, string password = "test-password-1!")
    {
        var response = await client.PostAsJsonAsync("/admin/api/login", new { username = "admin", password });
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Admin_api_requires_authentication()
    {
        var client = NewClient();
        var response = await client.GetAsync("/admin/api/repositories");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Wrong_password_is_rejected()
    {
        var client = NewClient();
        var response = await client.PostAsJsonAsync("/admin/api/login", new { username = "admin", password = "wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Mutations_require_the_csrf_header()
    {
        var client = NewClient();
        await LoginAsync(client);

        var without = await client.PostAsJsonAsync("/admin/api/packages", new { value = "Serilog" });
        Assert.Equal(HttpStatusCode.BadRequest, without.StatusCode);

        client.DefaultRequestHeaders.Add("X-DevAgent-Admin", "1");
        var with = await client.PostAsJsonAsync("/admin/api/packages", new { value = "Serilog" });
        Assert.Equal(HttpStatusCode.OK, with.StatusCode);
    }

    [Fact]
    public async Task Repository_crud_round_trips_and_is_recorded()
    {
        var client = NewClient();
        await LoginAsync(client);
        client.DefaultRequestHeaders.Add("X-DevAgent-Admin", "1");

        var create = await client.PostAsJsonAsync("/admin/api/repositories",
            new { key = "svc-new", cloneUrl = "https://git.internal/svc-new.git", baseBranch = "main" });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        var list = await client.GetStringAsync("/admin/api/repositories");
        Assert.Contains("svc-new", list);

        var changes = await client.GetStringAsync("/admin/api/config-changes");
        Assert.Contains("repositories", changes);
        Assert.Contains("svc-new", changes);

        var delete = await client.DeleteAsync("/admin/api/repositories/svc-new");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        Assert.DoesNotContain("svc-new", await client.GetStringAsync("/admin/api/repositories"));
    }

    [Fact]
    public async Task Repository_key_with_double_underscore_or_bad_url_is_rejected()
    {
        var client = NewClient();
        await LoginAsync(client);
        client.DefaultRequestHeaders.Add("X-DevAgent-Admin", "1");

        var badKey = await client.PostAsJsonAsync("/admin/api/repositories",
            new { key = "bad__key", cloneUrl = "https://git/x.git", baseBranch = "main" });
        Assert.Equal(HttpStatusCode.BadRequest, badKey.StatusCode);

        var badUrl = await client.PostAsJsonAsync("/admin/api/repositories",
            new { key = "ok", cloneUrl = "not a url", baseBranch = "main" });
        Assert.Equal(HttpStatusCode.BadRequest, badUrl.StatusCode);
    }

    [Fact]
    public async Task Mcp_server_registration_validates_key_and_endpoint()
    {
        var client = NewClient();
        await LoginAsync(client);
        client.DefaultRequestHeaders.Add("X-DevAgent-Admin", "1");

        var ok = await client.PostAsJsonAsync("/admin/api/mcp-servers", new
        {
            key = "advisories",
            name = "Advisories",
            endpoint = "https://mcp.internal/advisories",
            authHeaderName = "Authorization",
            authTokenEnvVar = "MCP_ADV_TOKEN",
            allowedTools = new[] { "query" },
            allowedPrompts = new[] { "summarize" },
            enabled = true,
        });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var badKey = await client.PostAsJsonAsync("/admin/api/mcp-servers", new
        {
            key = "bad__server",
            name = "x",
            endpoint = "https://x",
            allowedTools = Array.Empty<string>(),
            allowedPrompts = Array.Empty<string>(),
            enabled = true,
        });
        Assert.Equal(HttpStatusCode.BadRequest, badKey.StatusCode);

        var list = await client.GetStringAsync("/admin/api/mcp-servers");
        Assert.Contains("advisories", list);
        Assert.Contains("MCP_ADV_TOKEN", list); // env-var NAME is visible; no secret value exists to leak
    }

    [Fact]
    public async Task Webhook_secret_gates_the_public_webhook()
    {
        var client = NewClient();
        await LoginAsync(client);
        client.DefaultRequestHeaders.Add("X-DevAgent-Admin", "1");

        var set = await client.PostAsJsonAsync("/admin/api/webhooks",
            new { key = "nuget-package-published", enabled = true, sharedSecret = "hook-secret" });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        // The webhook list never returns the secret value.
        var hooks = await client.GetStringAsync("/admin/api/webhooks");
        Assert.DoesNotContain("hook-secret", hooks);
        Assert.Contains("hasSecret", hooks);

        // Public caller without the secret → 401.
        var anonymous = NewClient();
        var noSecret = await anonymous.PostAsJsonAsync("/hub/webhooks/nuget-package-published",
            new { packageId = "Serilog", version = "9.9.9" });
        Assert.Equal(HttpStatusCode.Unauthorized, noSecret.StatusCode);

        // With the secret → accepted (200; watch-list rejection is fine, the gate is what we test).
        anonymous.DefaultRequestHeaders.Add("X-DevAgent-Secret", "hook-secret");
        var withSecret = await anonymous.PostAsJsonAsync("/hub/webhooks/nuget-package-published",
            new { packageId = "Serilog", version = "9.9.9" });
        Assert.Equal(HttpStatusCode.OK, withSecret.StatusCode);

        // Disable the hook entirely → 403 even with the secret.
        var disable = await client.PostAsJsonAsync("/admin/api/webhooks",
            new { key = "nuget-package-published", enabled = false, sharedSecret = (string?)null });
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
        var disabled = await anonymous.PostAsJsonAsync("/hub/webhooks/nuget-package-published",
            new { packageId = "Serilog", version = "9.9.9" });
        Assert.Equal(HttpStatusCode.Forbidden, disabled.StatusCode);
    }

    [Fact]
    public async Task Agent_settings_and_audit_window_are_readable()
    {
        var client = NewClient();
        await LoginAsync(client);
        client.DefaultRequestHeaders.Add("X-DevAgent-Admin", "1");

        var agents = await client.GetStringAsync("/admin/api/agents");
        Assert.Contains("DependencyPilot", agents); // seeded on first run

        var update = await client.PostAsJsonAsync("/admin/api/agents/DependencyPilot", new
        {
            repositoryKeys = new[] { "svc-a" },
            watchedPackages = new[] { "Serilog" },
            targetFramework = (string?)null,
            includePrerelease = false,
            llmProvider = "Claude",
            llmModel = "claude-opus-4-8",
            mcpGrantsJson = """[{"serverKey":"advisories","tools":["query"],"prompts":[]}]""",
            skillKeys = new[] { "advisory-check" },
        });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var audit = await client.GetStringAsync("/admin/api/audit");
        Assert.Contains("config-agents-update", audit);
    }
}
