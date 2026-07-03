namespace Agents.ConfluenceGuide;

using DevAgent.Audit;
using DevAgent.Bridge.Confluence;
using Microsoft.Extensions.Options;

/// <summary>
/// Configuration for ConfluenceGuide: the Confluence connection (token by
/// env-var NAME) and which repositories' docs map to which page titles.
/// </summary>
public sealed class ConfluenceGuideOptions
{
    public const string SectionName = "ConfluenceGuide";

    public string BaseUrl { get; set; } = string.Empty;
    public string SpaceKey { get; set; } = string.Empty;
    public string? TokenEnvVar { get; set; }

    /// <summary>repository key → Confluence page title.</summary>
    public Dictionary<string, string> PagesByRepository { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One planned or performed page sync.</summary>
public sealed record ConfluenceSyncItem(string RepositoryKey, string PageTitle, string Action, string Detail);

/// <summary>
/// The ConfluenceGuide agent (planner tier this milestone): computes the
/// docs → page sync plan per configured repository and records it as audited
/// findings. When a connection is fully configured, it verifies page
/// existence via the read path; PAGE WRITES stay behind an explicit
/// publish call so the schedule alone never mutates a wiki.
///
/// SECURITY: All Confluence access happens ON THE HUB with the operator's
/// token reference. Sandboxes never receive Confluence credentials — the
/// sandbox produces docs in a PR; this agent mirrors REVIEWED content.
/// </summary>
public sealed class ConfluenceGuideService
{
    private readonly ConfluenceGuideOptions _options;
    private readonly IConfluenceClient _client;
    private readonly IAuditLog _audit;

    public ConfluenceGuideService(
        IOptions<ConfluenceGuideOptions> options,
        IConfluenceClient client,
        IAuditLog audit)
    {
        _options = options.Value;
        _client = client;
        _audit = audit;
    }

    /// <summary>Compute (and audit) the current sync plan. Read-only.</summary>
    public async Task<IReadOnlyList<ConfluenceSyncItem>> PlanSyncAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<ConfluenceSyncItem>();

        if (string.IsNullOrWhiteSpace(_options.BaseUrl) || _options.PagesByRepository.Count == 0)
        {
            return items;
        }

        var connection = new ConfluenceConnection
        {
            BaseUrl = _options.BaseUrl,
            SpaceKey = _options.SpaceKey,
            TokenEnvVar = _options.TokenEnvVar,
        };

        foreach (var (repositoryKey, pageTitle) in _options.PagesByRepository)
        {
            ConfluenceSyncItem item;
            try
            {
                var page = await _client.FindPageAsync(connection, pageTitle, cancellationToken);
                item = page is null
                    ? new ConfluenceSyncItem(repositoryKey, pageTitle, "create",
                        $"Page '{pageTitle}' does not exist yet in space '{_options.SpaceKey}'.")
                    : new ConfluenceSyncItem(repositoryKey, pageTitle, "update",
                        $"Page '{pageTitle}' exists (id {page.Id}, v{page.Version}).");
            }
            catch (HttpRequestException ex)
            {
                item = new ConfluenceSyncItem(repositoryKey, pageTitle, "error", $"Confluence unreachable: {ex.Message}");
            }

            await _audit.WriteAsync(new JobAuditEvent
            {
                JobId = $"confluence-{Guid.NewGuid():N}",
                Actor = "ConfluenceGuide",
                Status = "Planned",
                Message = $"[{repositoryKey}] {item.Action}: {item.Detail}",
            }, cancellationToken);

            items.Add(item);
        }

        return items;
    }

    /// <summary>
    /// Explicit publish of one repository's reviewed documentation content to
    /// its configured page. Never called by the schedule — only by an
    /// operator-facing endpoint.
    /// </summary>
    public async Task<ConfluenceSyncItem> PublishAsync(
        string repositoryKey, string storageHtml, CancellationToken cancellationToken = default)
    {
        if (!_options.PagesByRepository.TryGetValue(repositoryKey, out var pageTitle))
        {
            return new ConfluenceSyncItem(repositoryKey, "-", "rejected",
                $"Repository '{repositoryKey}' has no configured Confluence page.");
        }

        var connection = new ConfluenceConnection
        {
            BaseUrl = _options.BaseUrl,
            SpaceKey = _options.SpaceKey,
            TokenEnvVar = _options.TokenEnvVar,
        };

        ConfluenceSyncItem item;
        try
        {
            var page = await _client.UpsertPageAsync(connection, pageTitle, storageHtml, cancellationToken);
            item = new ConfluenceSyncItem(repositoryKey, pageTitle, "published",
                $"Page '{pageTitle}' is now v{page.Version} (id {page.Id}).");
        }
        catch (HttpRequestException ex)
        {
            item = new ConfluenceSyncItem(repositoryKey, pageTitle, "error", $"Publish failed: {ex.Message}");
        }

        await _audit.WriteAsync(new JobAuditEvent
        {
            JobId = $"confluence-{Guid.NewGuid():N}",
            Actor = "ConfluenceGuide",
            Status = item.Action == "published" ? "Succeeded" : "Failed",
            Message = $"[{repositoryKey}] {item.Detail}",
        }, cancellationToken);

        return item;
    }
}
