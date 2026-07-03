namespace Agents.SplunkSentinel;

using DevAgent.Audit;
using DevAgent.Bridge.Splunk;
using Microsoft.Extensions.Options;

/// <summary>One admin-configured search the sentinel runs on every sweep.</summary>
public sealed class SplunkWatch
{
    public string Name { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
}

/// <summary>
/// Configuration for SplunkSentinel: connection (token by env-var NAME) and
/// the searches to run. All admin-managed.
/// </summary>
public sealed class SplunkSentinelOptions
{
    public const string SectionName = "SplunkSentinel";

    public string BaseUrl { get; set; } = string.Empty;
    public string? TokenEnvVar { get; set; }
    public List<SplunkWatch> Searches { get; set; } = new();
}

/// <summary>The outcome of one search on one sweep.</summary>
public sealed record SplunkFinding(string Name, string Query, bool Succeeded, string Summary);

/// <summary>
/// The SplunkSentinel agent (observer tier this milestone): runs the
/// configured searches on a schedule and records every result as an audited
/// finding. It proposes no jobs and changes nothing — findings surface in the
/// audit log and the dashboard, where a human decides what to do.
/// </summary>
public sealed class SplunkSentinelService
{
    private const int SummaryChars = 500;

    private readonly SplunkSentinelOptions _options;
    private readonly ISplunkSearchClient _client;
    private readonly IAuditLog _audit;

    public SplunkSentinelService(
        IOptions<SplunkSentinelOptions> options,
        ISplunkSearchClient client,
        IAuditLog audit)
    {
        _options = options.Value;
        _client = client;
        _audit = audit;
    }

    public async Task<IReadOnlyList<SplunkFinding>> SweepAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl) || _options.Searches.Count == 0)
        {
            return Array.Empty<SplunkFinding>();
        }

        var connection = new SplunkConnection
        {
            BaseUrl = _options.BaseUrl,
            TokenEnvVar = _options.TokenEnvVar,
        };

        var findings = new List<SplunkFinding>();
        foreach (var watch in _options.Searches)
        {
            SplunkFinding finding;
            try
            {
                var result = await _client.OneshotSearchAsync(connection, watch.Query, cancellationToken);
                var summary = result.Length <= SummaryChars ? result : result[..SummaryChars] + "…";
                finding = new SplunkFinding(watch.Name, watch.Query, Succeeded: true, summary);
            }
            catch (HttpRequestException ex)
            {
                finding = new SplunkFinding(watch.Name, watch.Query, Succeeded: false, $"Search failed: {ex.Message}");
            }

            await _audit.WriteAsync(new JobAuditEvent
            {
                JobId = $"splunk-{Guid.NewGuid():N}",
                Actor = "SplunkSentinel",
                Status = finding.Succeeded ? "Observed" : "Failed",
                Message = $"[{finding.Name}] {finding.Summary}",
            }, cancellationToken);

            findings.Add(finding);
        }

        return findings;
    }
}
