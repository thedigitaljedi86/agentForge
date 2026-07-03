namespace Agents.ConfluenceGuide.Tests;

using System.Net;
using System.Text;
using Agents.ConfluenceGuide;
using DevAgent.Audit;
using DevAgent.Bridge.Confluence;
using Microsoft.Extensions.Options;
using Xunit;

public class ConfluenceGuideTests
{
    [Fact]
    public async Task Unconfigured_guide_plans_nothing()
    {
        var client = new FakeConfluenceClient();
        var service = new ConfluenceGuideService(
            Options.Create(new ConfluenceGuideOptions()), client, new ConsoleAuditLog());

        var plan = await service.PlanSyncAsync();

        Assert.Empty(plan);
        Assert.Empty(client.FindCalls);
    }

    [Fact]
    public async Task Plan_distinguishes_create_and_update_and_is_read_only()
    {
        var client = new FakeConfluenceClient();
        client.ExistingPages["Service A docs"] = new ConfluencePage("123", "Service A docs", 4);

        var options = new ConfluenceGuideOptions
        {
            BaseUrl = "https://confluence.internal",
            SpaceKey = "DEV",
            PagesByRepository =
            {
                ["svc-a"] = "Service A docs",
                ["svc-b"] = "Service B docs",
            },
        };

        var service = new ConfluenceGuideService(Options.Create(options), client, new ConsoleAuditLog());
        var plan = await service.PlanSyncAsync();

        Assert.Equal(2, plan.Count);
        Assert.Equal("update", plan.Single(p => p.RepositoryKey == "svc-a").Action);
        Assert.Equal("create", plan.Single(p => p.RepositoryKey == "svc-b").Action);
        Assert.Empty(client.Upserts); // planning never writes
    }

    [Fact]
    public async Task Publish_is_explicit_and_only_for_configured_repositories()
    {
        var client = new FakeConfluenceClient();
        var options = new ConfluenceGuideOptions
        {
            BaseUrl = "https://confluence.internal",
            SpaceKey = "DEV",
            PagesByRepository = { ["svc-a"] = "Service A docs" },
        };
        var service = new ConfluenceGuideService(Options.Create(options), client, new ConsoleAuditLog());

        var rejected = await service.PublishAsync("unknown-repo", "<p>docs</p>");
        Assert.Equal("rejected", rejected.Action);
        Assert.Empty(client.Upserts);

        var published = await service.PublishAsync("svc-a", "<p>docs</p>");
        Assert.Equal("published", published.Action);
        Assert.Single(client.Upserts);
    }

    [Fact]
    public async Task Http_client_creates_a_missing_page_with_bearer_token_from_env_reference()
    {
        var requests = new List<(HttpMethod Method, string Url, string? Auth, string? Body)>();
        var handler = new FakeHandler(req =>
        {
            var body = req.Content is null ? null : req.Content.ReadAsStringAsync().Result;
            requests.Add((req.Method, req.RequestUri!.ToString(), req.Headers.Authorization?.ToString(), body));

            // Find → empty; create → a page document.
            return req.Method == HttpMethod.Get
                ? Json("""{"results":[]}""")
                : Json("""{"id":"777","title":"Service A docs","version":{"number":1}}""");
        });

        var client = new HttpConfluenceClient(new HttpClient(handler), name => name == "CONF_TOKEN" ? "tok" : null);
        var connection = new ConfluenceConnection
        {
            BaseUrl = "https://confluence.internal",
            SpaceKey = "DEV",
            TokenEnvVar = "CONF_TOKEN",
        };

        var page = await client.UpsertPageAsync(connection, "Service A docs", "<p>hi</p>");

        Assert.Equal("777", page.Id);
        Assert.Equal(2, requests.Count);
        Assert.Equal(HttpMethod.Post, requests[1].Method);
        Assert.All(requests, r => Assert.Equal("Bearer tok", r.Auth));
        Assert.Contains("\"DEV\"", requests[1].Body);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    // --- fakes ---

    private sealed class FakeConfluenceClient : IConfluenceClient
    {
        public Dictionary<string, ConfluencePage> ExistingPages { get; } = new(StringComparer.Ordinal);
        public List<string> FindCalls { get; } = new();
        public List<(string Title, string Html)> Upserts { get; } = new();

        public Task<ConfluencePage?> FindPageAsync(ConfluenceConnection connection, string title, CancellationToken ct = default)
        {
            FindCalls.Add(title);
            return Task.FromResult(ExistingPages.GetValueOrDefault(title));
        }

        public Task<ConfluencePage> UpsertPageAsync(ConfluenceConnection connection, string title, string storageHtml, CancellationToken ct = default)
        {
            Upserts.Add((title, storageHtml));
            var page = ExistingPages.TryGetValue(title, out var existing)
                ? existing with { Version = existing.Version + 1 }
                : new ConfluencePage("new-1", title, 1);
            return Task.FromResult(page);
        }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_respond(request));
    }
}
