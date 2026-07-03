namespace DevAgent.Hub.Api.Admin;

using DevAgent.Audit;
using DevAgent.Store;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The admin console's API: CRUD over every admin-managed section of the
/// platform store. All endpoints require the admin login; every mutating
/// request must also carry the X-DevAgent-Admin header (CSRF defence); and
/// every change is recorded twice — in the store's ConfigChanges table and as
/// a DecisionAuditEvent in the audit trail.
///
/// SECURITY: This API edits ALLOWLISTS — it never executes anything. The worst
/// a compromised admin session can do is widen future policy, and every such
/// widening is attributable, timestamped evidence.
/// </summary>
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/admin/api").RequireAuthorization();

        // CSRF gate for anything that isn't a read.
        admin.AddEndpointFilter(async (ctx, next) =>
        {
            var method = ctx.HttpContext.Request.Method;
            if (!HttpMethods.IsGet(method) && !ctx.HttpContext.Request.Headers.ContainsKey(AdminAuth.HeaderName))
            {
                return Results.BadRequest(new { error = $"Missing {AdminAuth.HeaderName} header." });
            }

            return await next(ctx);
        });

        MapRepositories(admin);
        MapSimpleList(admin, "packages",
            db => db.Packages.Select(p => p.PackageId),
            (db, v) => db.Packages.Add(new PackageEntity { PackageId = v }),
            (db, v) => db.Packages.Where(p => p.PackageId == v).ExecuteDeleteAsync());
        MapSimpleList(admin, "images",
            db => db.ContainerImages.Select(i => i.Image),
            (db, v) => db.ContainerImages.Add(new ContainerImageEntity { Image = v }),
            (db, v) => db.ContainerImages.Where(i => i.Image == v).ExecuteDeleteAsync());
        MapSimpleList(admin, "frameworks",
            db => db.TargetFrameworks.Select(f => f.Framework),
            (db, v) => db.TargetFrameworks.Add(new TargetFrameworkEntity { Framework = v }),
            (db, v) => db.TargetFrameworks.Where(f => f.Framework == v).ExecuteDeleteAsync());
        MapJobTypeImages(admin);
        MapCiConnections(admin);
        MapPackageUsage(admin);
        MapMcpServers(admin);
        MapSkills(admin);
        MapWebhooks(admin);
        MapAgents(admin);
        MapAudit(admin);
    }

    // ---------- repositories ----------

    public sealed record RepositoryBody(string Key, string CloneUrl, string BaseBranch);

    private static void MapRepositories(RouteGroupBuilder admin)
    {
        admin.MapGet("/repositories", async (IDbContextFactory<DevAgentDbContext> f, CancellationToken ct) =>
        {
            await using var db = await f.CreateDbContextAsync(ct);
            return Results.Ok(await db.Repositories.AsNoTracking().OrderBy(r => r.Key).ToListAsync(ct));
        });

        admin.MapPost("/repositories", async (
            RepositoryBody body, HttpContext http,
            IDbContextFactory<DevAgentDbContext> f, IAuditLog audit, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Key) || body.Key.Contains("__"))
            {
                return Results.BadRequest(new { error = "Key is required and may not contain '__'." });
            }

            if (!Uri.TryCreate(body.CloneUrl, UriKind.Absolute, out _))
            {
                return Results.BadRequest(new { error = "CloneUrl must be an absolute URL." });
            }

            await using var db = await f.CreateDbContextAsync(ct);
            var existing = await db.Repositories.FindAsync(new object[] { body.Key }, ct);
            if (existing is null)
            {
                db.Repositories.Add(new RepositoryEntity { Key = body.Key, CloneUrl = body.CloneUrl, BaseBranch = body.BaseBranch });
            }
            else
            {
                existing.CloneUrl = body.CloneUrl;
                existing.BaseBranch = body.BaseBranch;
            }

            await RecordAsync(db, audit, http, "repositories", existing is null ? "add" : "update",
                $"{body.Key} → {body.CloneUrl} ({body.BaseBranch})", ct);
            return Results.Ok();
        });

        admin.MapDelete("/repositories/{key}", async (
            string key, HttpContext http,
            IDbContextFactory<DevAgentDbContext> f, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await f.CreateDbContextAsync(ct);
            await db.Repositories.Where(r => r.Key == key).ExecuteDeleteAsync(ct);
            await RecordAsync(db, audit, http, "repositories", "delete", key, ct);
            return Results.Ok();
        });
    }

    // ---------- generic single-value lists (packages, images, frameworks) ----------

    public sealed record ValueBody(string Value);

    private static void MapSimpleList(
        RouteGroupBuilder admin,
        string section,
        Func<DevAgentDbContext, IQueryable<string>> query,
        Action<DevAgentDbContext, string> add,
        Func<DevAgentDbContext, string, Task<int>> delete)
    {
        admin.MapGet($"/{section}", async (IDbContextFactory<DevAgentDbContext> f, CancellationToken ct) =>
        {
            await using var db = await f.CreateDbContextAsync(ct);
            return Results.Ok(await query(db).AsNoTracking().OrderBy(v => v).ToListAsync(ct));
        });

        admin.MapPost($"/{section}", async (
            ValueBody body, HttpContext http,
            IDbContextFactory<DevAgentDbContext> f, IAuditLog audit, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Value))
            {
                return Results.BadRequest(new { error = "Value is required." });
            }

            await using var db = await f.CreateDbContextAsync(ct);
            if (!await query(db).AnyAsync(v => v == body.Value, ct))
            {
                add(db, body.Value.Trim());
                await RecordAsync(db, audit, http, section, "add", body.Value, ct);
            }

            return Results.Ok();
        });

        admin.MapDelete($"/{section}/{{value}}", async (
            string value, HttpContext http,
            IDbContextFactory<DevAgentDbContext> f, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await f.CreateDbContextAsync(ct);
            await delete(db, value);
            await RecordAsync(db, audit, http, section, "delete", value, ct);
            return Results.Ok();
        });
    }

    // ---------- job type → image map ----------

    public sealed record JobTypeImageBody(string JobType, string Image);

    private static void MapJobTypeImages(RouteGroupBuilder admin)
    {
        admin.MapGet("/jobtype-images", async (IDbContextFactory<DevAgentDbContext> f, CancellationToken ct) =>
        {
            await using var db = await f.CreateDbContextAsync(ct);
            return Results.Ok(await db.JobTypeImages.AsNoTracking().OrderBy(j => j.JobType).ToListAsync(ct));
        });

        admin.MapPost("/jobtype-images", async (
            JobTypeImageBody body, HttpContext http,
            IDbContextFactory<DevAgentDbContext> f, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await f.CreateDbContextAsync(ct);
            var existing = await db.JobTypeImages.FindAsync(new object[] { body.JobType }, ct);
            if (existing is null)
            {
                db.JobTypeImages.Add(new JobTypeImageEntity { JobType = body.JobType, Image = body.Image });
            }
            else
            {
                existing.Image = body.Image;
            }

            await RecordAsync(db, audit, http, "jobtype-images", "set", $"{body.JobType} → {body.Image}", ct);
            return Results.Ok();
        });
    }

    // ---------- CI connections (PipelineDoctor) ----------

    public sealed record CiConnectionBody(
        string RepositoryKey, string Provider, string BaseUrl, string ProjectPath, string? TokenEnvVar);

    private static readonly string[] CiProviders = { "GitHubActions", "GitLabCi", "AzureDevOpsPipelines" };

    private static void MapCiConnections(RouteGroupBuilder admin)
    {
        admin.MapGet("/ci-connections", async (IDbContextFactory<DevAgentDbContext> f, CancellationToken ct) =>
        {
            await using var db = await f.CreateDbContextAsync(ct);
            // TokenEnvVar is a REFERENCE (env var name), not a secret value.
            return Results.Ok(await db.CiConnections.AsNoTracking().OrderBy(c => c.RepositoryKey).ToListAsync(ct));
        });

        admin.MapPost("/ci-connections", async (
            CiConnectionBody body, HttpContext http,
            IDbContextFactory<DevAgentDbContext> f, IAuditLog audit, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.RepositoryKey))
            {
                return Results.BadRequest(new { error = "RepositoryKey is required." });
            }

            if (!CiProviders.Contains(body.Provider, StringComparer.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { error = $"Provider must be one of: {string.Join(", ", CiProviders)}." });
            }

            if (!Uri.TryCreate(body.BaseUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != "https" && uri.Scheme != "http"))
            {
                return Results.BadRequest(new { error = "BaseUrl must be an absolute http(s) URL." });
            }

            if (string.IsNullOrWhiteSpace(body.ProjectPath))
            {
                return Results.BadRequest(new { error = "ProjectPath is required (owner/repo, group/project or org/project)." });
            }

            await using var db = await f.CreateDbContextAsync(ct);
            var existing = await db.CiConnections.FindAsync(new object[] { body.RepositoryKey }, ct);
            var entity = existing ?? new CiConnectionEntity { RepositoryKey = body.RepositoryKey };
            entity.Provider = CiProviders.First(p => p.Equals(body.Provider, StringComparison.OrdinalIgnoreCase));
            entity.BaseUrl = body.BaseUrl.TrimEnd('/');
            entity.ProjectPath = body.ProjectPath.Trim();
            entity.TokenEnvVar = string.IsNullOrWhiteSpace(body.TokenEnvVar) ? null : body.TokenEnvVar!.Trim();
            if (existing is null)
            {
                db.CiConnections.Add(entity);
            }

            await RecordAsync(db, audit, http, "ci-connections", existing is null ? "add" : "update",
                $"{body.RepositoryKey} → {entity.Provider} {entity.BaseUrl} ({entity.ProjectPath}) token-env={entity.TokenEnvVar ?? "-"}", ct);
            return Results.Ok();
        });

        admin.MapDelete("/ci-connections/{repositoryKey}", async (
            string repositoryKey, HttpContext http,
            IDbContextFactory<DevAgentDbContext> f, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await f.CreateDbContextAsync(ct);
            await db.CiConnections.Where(c => c.RepositoryKey == repositoryKey).ExecuteDeleteAsync(ct);
            await RecordAsync(db, audit, http, "ci-connections", "delete", repositoryKey, ct);
            return Results.Ok();
        });
    }

    // ---------- package usage map ----------

    public sealed record UsageBody(string RepositoryKey, string PackageId, string? CurrentVersion);

    private static void MapPackageUsage(RouteGroupBuilder admin)
    {
        admin.MapGet("/package-usage", async (IDbContextFactory<DevAgentDbContext> f, CancellationToken ct) =>
        {
            await using var db = await f.CreateDbContextAsync(ct);
            return Results.Ok(await db.PackageUsages.AsNoTracking()
                .OrderBy(u => u.RepositoryKey).ThenBy(u => u.PackageId).ToListAsync(ct));
        });

        admin.MapPost("/package-usage", async (
            UsageBody body, HttpContext http,
            IDbContextFactory<DevAgentDbContext> f, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await f.CreateDbContextAsync(ct);
            var existing = await db.PackageUsages.FindAsync(new object[] { body.RepositoryKey, body.PackageId }, ct);
            if (existing is null)
            {
                db.PackageUsages.Add(new PackageUsageEntity
                {
                    RepositoryKey = body.RepositoryKey,
                    PackageId = body.PackageId,
                    CurrentVersion = body.CurrentVersion,
                });
            }
            else
            {
                existing.CurrentVersion = body.CurrentVersion;
            }

            await RecordAsync(db, audit, http, "package-usage", "set",
                $"{body.RepositoryKey} uses {body.PackageId}@{body.CurrentVersion ?? "?"}", ct);
            return Results.Ok();
        });

        admin.MapDelete("/package-usage/{repositoryKey}/{packageId}", async (
            string repositoryKey, string packageId, HttpContext http,
            IDbContextFactory<DevAgentDbContext> f, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await f.CreateDbContextAsync(ct);
            await db.PackageUsages
                .Where(u => u.RepositoryKey == repositoryKey && u.PackageId == packageId)
                .ExecuteDeleteAsync(ct);
            await RecordAsync(db, audit, http, "package-usage", "delete", $"{repositoryKey}/{packageId}", ct);
            return Results.Ok();
        });
    }

    // ---------- MCP servers ----------

    public sealed record McpServerBody(
        string Key, string Name, string Endpoint,
        string? AuthHeaderName, string? AuthTokenEnvVar,
        string[] AllowedTools, string[] AllowedPrompts, bool Enabled);

    private static void MapMcpServers(RouteGroupBuilder admin)
    {
        admin.MapGet("/mcp-servers", async (IDbContextFactory<DevAgentDbContext> f, CancellationToken ct) =>
        {
            await using var db = await f.CreateDbContextAsync(ct);
            var servers = await db.McpServers.AsNoTracking().OrderBy(s => s.Key).ToListAsync(ct);
            return Results.Ok(servers.Select(s => new
            {
                s.Key,
                s.Name,
                s.Endpoint,
                s.AuthHeaderName,
                s.AuthTokenEnvVar, // a REFERENCE (env var name), not a secret
                AllowedTools = JsonColumns.ToList(s.AllowedToolsJson),
                AllowedPrompts = JsonColumns.ToList(s.AllowedPromptsJson),
                s.Enabled,
            }));
        });

        admin.MapPost("/mcp-servers", async (
            McpServerBody body, HttpContext http,
            IDbContextFactory<DevAgentDbContext> f, IAuditLog audit, CancellationToken ct) =>
        {
            // "__" is the wire-name separator (mcp__{key}__{tool}) — a key
            // containing it could impersonate another server's tools.
            if (string.IsNullOrWhiteSpace(body.Key) || body.Key.Contains("__"))
            {
                return Results.BadRequest(new { error = "Key is required and may not contain '__'." });
            }

            if (!Uri.TryCreate(body.Endpoint, UriKind.Absolute, out var uri)
                || (uri.Scheme != "https" && uri.Scheme != "http"))
            {
                return Results.BadRequest(new { error = "Endpoint must be an absolute http(s) URL." });
            }

            await using var db = await f.CreateDbContextAsync(ct);
            var existing = await db.McpServers.FindAsync(new object[] { body.Key }, ct);
            var entity = existing ?? new McpServerEntity { Key = body.Key };
            entity.Name = body.Name;
            entity.Endpoint = body.Endpoint;
            entity.AuthHeaderName = body.AuthHeaderName;
            entity.AuthTokenEnvVar = body.AuthTokenEnvVar;
            entity.AllowedToolsJson = JsonColumns.FromList(body.AllowedTools);
            entity.AllowedPromptsJson = JsonColumns.FromList(body.AllowedPrompts);
            entity.Enabled = body.Enabled;
            if (existing is null)
            {
                db.McpServers.Add(entity);
            }

            await RecordAsync(db, audit, http, "mcp-servers", existing is null ? "add" : "update",
                $"{body.Key} → {body.Endpoint} tools=[{string.Join(",", body.AllowedTools)}] prompts=[{string.Join(",", body.AllowedPrompts)}] enabled={body.Enabled}", ct);
            return Results.Ok();
        });

        admin.MapDelete("/mcp-servers/{key}", async (
            string key, HttpContext http,
            IDbContextFactory<DevAgentDbContext> f, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await f.CreateDbContextAsync(ct);
            await db.McpServers.Where(s => s.Key == key).ExecuteDeleteAsync(ct);
            await RecordAsync(db, audit, http, "mcp-servers", "delete", key, ct);
            return Results.Ok();
        });
    }

    // ---------- skills ----------

    public sealed record SkillBody(
        string Key, string Name, string Description, string Instructions,
        string? McpServerKey, string? McpPromptName, string? McpPromptArgsJson,
        string[] RequiredTools, bool Enabled);

    private static void MapSkills(RouteGroupBuilder admin)
    {
        admin.MapGet("/skills", async (IDbContextFactory<DevAgentDbContext> f, CancellationToken ct) =>
        {
            await using var db = await f.CreateDbContextAsync(ct);
            var skills = await db.Skills.AsNoTracking().OrderBy(s => s.Key).ToListAsync(ct);
            return Results.Ok(skills.Select(s => new
            {
                s.Key,
                s.Name,
                s.Description,
                s.Instructions,
                s.McpServerKey,
                s.McpPromptName,
                s.McpPromptArgsJson,
                RequiredTools = JsonColumns.ToList(s.RequiredToolsJson),
                s.Enabled,
            }));
        });

        admin.MapPost("/skills", async (
            SkillBody body, HttpContext http,
            IDbContextFactory<DevAgentDbContext> f, IAuditLog audit, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Key))
            {
                return Results.BadRequest(new { error = "Key is required." });
            }

            await using var db = await f.CreateDbContextAsync(ct);
            var existing = await db.Skills.FindAsync(new object[] { body.Key }, ct);
            var entity = existing ?? new SkillEntity { Key = body.Key };
            entity.Name = body.Name;
            entity.Description = body.Description;
            entity.Instructions = body.Instructions;
            entity.McpServerKey = body.McpServerKey;
            entity.McpPromptName = body.McpPromptName;
            entity.McpPromptArgsJson = string.IsNullOrWhiteSpace(body.McpPromptArgsJson) ? "{}" : body.McpPromptArgsJson!;
            entity.RequiredToolsJson = JsonColumns.FromList(body.RequiredTools);
            entity.Enabled = body.Enabled;
            if (existing is null)
            {
                db.Skills.Add(entity);
            }

            await RecordAsync(db, audit, http, "skills", existing is null ? "add" : "update",
                $"{body.Key} requires=[{string.Join(",", body.RequiredTools)}] enabled={body.Enabled}", ct);
            return Results.Ok();
        });

        admin.MapDelete("/skills/{key}", async (
            string key, HttpContext http,
            IDbContextFactory<DevAgentDbContext> f, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await f.CreateDbContextAsync(ct);
            await db.Skills.Where(s => s.Key == key).ExecuteDeleteAsync(ct);
            await RecordAsync(db, audit, http, "skills", "delete", key, ct);
            return Results.Ok();
        });
    }

    // ---------- webhooks ----------

    public sealed record WebhookBody(string Key, bool Enabled, string? SharedSecret);

    private static void MapWebhooks(RouteGroupBuilder admin)
    {
        admin.MapGet("/webhooks", async (IDbContextFactory<DevAgentDbContext> f, CancellationToken ct) =>
        {
            await using var db = await f.CreateDbContextAsync(ct);
            var hooks = await db.Webhooks.AsNoTracking().OrderBy(w => w.Key).ToListAsync(ct);
            // The shared secret is write-only: report only whether one is set.
            return Results.Ok(hooks.Select(w => new { w.Key, w.Enabled, HasSecret = !string.IsNullOrEmpty(w.SharedSecret) }));
        });

        admin.MapPost("/webhooks", async (
            WebhookBody body, HttpContext http,
            IDbContextFactory<DevAgentDbContext> f, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await f.CreateDbContextAsync(ct);
            var existing = await db.Webhooks.FindAsync(new object[] { body.Key }, ct);
            var entity = existing ?? new WebhookEntity { Key = body.Key };
            entity.Enabled = body.Enabled;
            if (body.SharedSecret is not null) // null = keep existing secret
            {
                entity.SharedSecret = body.SharedSecret.Length == 0 ? null : body.SharedSecret;
            }

            if (existing is null)
            {
                db.Webhooks.Add(entity);
            }

            await RecordAsync(db, audit, http, "webhooks", "update",
                $"{body.Key} enabled={body.Enabled} secret={(body.SharedSecret is null ? "unchanged" : body.SharedSecret.Length == 0 ? "cleared" : "set")}", ct);
            return Results.Ok();
        });
    }

    // ---------- agents (watch lists, LLM pin, MCP grants, skills) ----------

    public sealed record AgentBody(
        string[] RepositoryKeys, string[] WatchedPackages, string? TargetFramework,
        bool IncludePrerelease, string? LlmProvider, string? LlmModel,
        string McpGrantsJson, string[] SkillKeys);

    private static void MapAgents(RouteGroupBuilder admin)
    {
        admin.MapGet("/agents", async (IDbContextFactory<DevAgentDbContext> f, CancellationToken ct) =>
        {
            await using var db = await f.CreateDbContextAsync(ct);
            var agents = await db.AgentSettings.AsNoTracking().OrderBy(a => a.AgentName).ToListAsync(ct);
            return Results.Ok(agents.Select(a => new
            {
                a.AgentName,
                RepositoryKeys = JsonColumns.ToList(a.RepositoryKeysJson),
                WatchedPackages = JsonColumns.ToList(a.WatchedPackagesJson),
                a.TargetFramework,
                a.IncludePrerelease,
                a.LlmProvider,
                a.LlmModel,
                McpGrantsJson = a.McpGrantsJson,
                SkillKeys = JsonColumns.ToList(a.SkillKeysJson),
            }));
        });

        admin.MapPost("/agents/{name}", async (
            string name, AgentBody body, HttpContext http,
            IDbContextFactory<DevAgentDbContext> f, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await f.CreateDbContextAsync(ct);
            var existing = await db.AgentSettings.FindAsync(new object[] { name }, ct);
            var entity = existing ?? new AgentSettingEntity { AgentName = name };
            entity.RepositoryKeysJson = JsonColumns.FromList(body.RepositoryKeys);
            entity.WatchedPackagesJson = JsonColumns.FromList(body.WatchedPackages);
            entity.TargetFramework = body.TargetFramework;
            entity.IncludePrerelease = body.IncludePrerelease;
            entity.LlmProvider = body.LlmProvider;
            entity.LlmModel = body.LlmModel;
            entity.McpGrantsJson = string.IsNullOrWhiteSpace(body.McpGrantsJson) ? "[]" : body.McpGrantsJson;
            entity.SkillKeysJson = JsonColumns.FromList(body.SkillKeys);
            if (existing is null)
            {
                db.AgentSettings.Add(entity);
            }

            await RecordAsync(db, audit, http, "agents", "update",
                $"{name}: repos=[{string.Join(",", body.RepositoryKeys)}] llm={body.LlmProvider ?? "off"}/{body.LlmModel ?? "-"} skills=[{string.Join(",", body.SkillKeys)}]", ct);
            return Results.Ok();
        });
    }

    // ---------- audit windows ----------

    private static void MapAudit(RouteGroupBuilder admin)
    {
        admin.MapGet("/audit", (InMemoryRingAuditLog ring) =>
            Results.Ok(ring.Snapshot().Select(e => new
            {
                e.EventId,
                e.TimestampUtc,
                e.JobId,
                e.Actor,
                Kind = e.Kind.ToString(),
                Summary = e switch
                {
                    DecisionAuditEvent d => $"{d.Decision}: {(d.Allowed ? "ALLOW" : "DENY")}{(d.Reason is null ? "" : $" — {d.Reason}")}",
                    JobAuditEvent j => $"{j.Status}{(j.Message is null ? "" : $" — {j.Message}")}",
                    ToolCallAuditEvent t => $"{t.ToolName} {(t.Allowed ? "allowed" : "DENIED")}{(t.DenyReason is null ? "" : $" — {t.DenyReason}")}",
                    DiffAuditEvent d => $"diff {d.FilePath} ({d.UnifiedDiff.Length} bytes)",
                    PromptAuditEvent p => $"prompt ({p.Prompt.Length} chars)",
                    _ => e.ToString() ?? "",
                },
            })));

        admin.MapGet("/config-changes", async (IDbContextFactory<DevAgentDbContext> f, CancellationToken ct) =>
        {
            await using var db = await f.CreateDbContextAsync(ct);
            return Results.Ok(await db.ConfigChanges.AsNoTracking()
                .OrderByDescending(c => c.Id).Take(200).ToListAsync(ct));
        });
    }

    // ---------- shared ----------

    private static async Task RecordAsync(
        DevAgentDbContext db, IAuditLog audit, HttpContext http,
        string section, string action, string details, CancellationToken ct)
    {
        var user = http.User.Identity?.Name ?? "unknown";
        db.ConfigChanges.Add(new ConfigChangeEntity
        {
            User = user,
            Section = section,
            Action = action,
            Details = details,
        });
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(new DecisionAuditEvent
        {
            Actor = $"admin:{user}",
            Decision = $"config-{section}-{action}",
            Allowed = true,
            Reason = details,
        }, ct);
    }
}
