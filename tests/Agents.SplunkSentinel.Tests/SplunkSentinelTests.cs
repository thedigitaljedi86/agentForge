namespace Agents.SplunkSentinel.Tests;

using System.Net;
using Agents.SplunkSentinel;
using DevAgent.Audit;
using DevAgent.Bridge.Splunk;
using Microsoft.Extensions.Options;
using Xunit;

public class SplunkSentinelTests
{
    [Fact]
    public async Task Unconfigured_sentinel_is_a_quiet_no_op()
    {
        var client = new RecordingSplunkClient();
        var service = new SplunkSentinelService(
            Options.Create(new SplunkSentinelOptions()), client, new ConsoleAuditLog());

        var findings = await service.SweepAsync();

        Assert.Empty(findings);
        Assert.Empty(client.Queries);
    }

    [Fact]
    public async Task Every_configured_search_runs_and_is_audited()
    {
        var client = new RecordingSplunkClient { Result = """{"results":[{"count":"3"}]}""" };
        var audit = new RecordingAuditLog();
        var options = new SplunkSentinelOptions
        {
            BaseUrl = "https://splunk.internal:8089",
            TokenEnvVar = "SPLUNK_TOKEN",
            Searches =
            {
                new SplunkWatch { Name = "errors", Query = "index=app level=ERROR | stats count" },
                new SplunkWatch { Name = "latency", Query = "index=app | stats avg(ms)" },
            },
        };

        var service = new SplunkSentinelService(Options.Create(options), client, audit);
        var findings = await service.SweepAsync();

        Assert.Equal(2, findings.Count);
        Assert.All(findings, f => Assert.True(f.Succeeded));
        Assert.Equal(2, client.Queries.Count);
        Assert.Equal(2, audit.Events.Count);
        Assert.All(audit.Events, e => Assert.Equal("SplunkSentinel", e.Actor));
    }

    [Fact]
    public async Task A_failing_search_is_recorded_not_thrown()
    {
        var client = new RecordingSplunkClient { Throw = true };
        var audit = new RecordingAuditLog();
        var options = new SplunkSentinelOptions
        {
            BaseUrl = "https://splunk.internal:8089",
            Searches = { new SplunkWatch { Name = "errors", Query = "index=app" } },
        };

        var service = new SplunkSentinelService(Options.Create(options), client, audit);
        var findings = await service.SweepAsync();

        var finding = Assert.Single(findings);
        Assert.False(finding.Succeeded);
        Assert.Contains("failed", finding.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Http_client_sends_bearer_token_from_env_reference_only()
    {
        string? capturedAuth = null;
        string? capturedBody = null;
        var handler = new FakeHandler(req =>
        {
            capturedAuth = req.Headers.Authorization?.ToString();
            capturedBody = req.Content!.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"results":[]}""") };
        });

        var client = new HttpSplunkSearchClient(new HttpClient(handler), name => name == "SPLUNK_TOKEN" ? "secret-token" : null);
        var connection = new SplunkConnection { BaseUrl = "https://splunk:8089", TokenEnvVar = "SPLUNK_TOKEN" };

        var result = await client.OneshotSearchAsync(connection, "index=app | stats count");

        Assert.Equal("Bearer secret-token", capturedAuth);
        Assert.Contains("output_mode=json", capturedBody);
        Assert.Contains("results", result);
    }

    // --- fakes ---

    private sealed class RecordingSplunkClient : ISplunkSearchClient
    {
        public List<string> Queries { get; } = new();
        public string Result { get; set; } = "{}";
        public bool Throw { get; set; }

        public Task<string> OneshotSearchAsync(SplunkConnection connection, string query, CancellationToken ct = default)
        {
            if (Throw)
            {
                throw new HttpRequestException("splunk unreachable");
            }

            Queries.Add(query);
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingAuditLog : IAuditLog
    {
        public List<AuditEvent> Events { get; } = new();

        public Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            return Task.CompletedTask;
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
