namespace DevAgent.Runner.Api.Mcp;

using System.Text;
using System.Text.Json;
using DevAgent.Audit;
using DevAgent.Bridge.Mcp;
using DevAgent.Contracts.Sandbox;
using DevAgent.Store;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Enriches a fully-validated sandbox request with the requesting AGENT's
/// capabilities, resolved entirely server-side from the admin store:
///   * the agent's pinned LLM provider/model,
///   * its granted MCP tools (registry ∩ grant, with live schemas),
///   * a per-job gateway token,
///   * its skills' instructions (inline markdown or MCP-prompt-backed).
/// API callers cannot influence any of this — only which agent a job belongs
/// to, and agents get exactly what an administrator configured.
/// </summary>
public interface ISandboxJobEnricher
{
    Task<SandboxJobRequest> EnrichAsync(SandboxJobRequest request, string requestedBy, CancellationToken ct = default);
}

/// <summary>Pass-through used when no store is configured (config-only mode).</summary>
public sealed class NullSandboxJobEnricher : ISandboxJobEnricher
{
    public Task<SandboxJobRequest> EnrichAsync(SandboxJobRequest request, string requestedBy, CancellationToken ct = default)
        => Task.FromResult(request);
}

public sealed class StoreSandboxJobEnricher : ISandboxJobEnricher
{
    // The built-in Forge tools every agent has; used to validate skill
    // requirements without referencing the Forge assembly.
    private static readonly HashSet<string> BuiltinTools = new(StringComparer.Ordinal)
    {
        "list_files", "read_file", "apply_patch", "replace_file",
        "run_dotnet_build", "run_dotnet_test", "git_status",
    };

    private readonly IDbContextFactory<DevAgentDbContext> _dbFactory;
    private readonly IMcpJobTokenStore _tokens;
    private readonly IMcpClient _mcp;
    private readonly IAuditLog _audit;

    public StoreSandboxJobEnricher(
        IDbContextFactory<DevAgentDbContext> dbFactory,
        IMcpJobTokenStore tokens,
        IMcpClient mcp,
        IAuditLog audit)
    {
        _dbFactory = dbFactory;
        _tokens = tokens;
        _mcp = mcp;
        _audit = audit;
    }

    public async Task<SandboxJobRequest> EnrichAsync(SandboxJobRequest request, string requestedBy, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var agent = await db.AgentSettings.AsNoTracking()
            .FirstOrDefaultAsync(a => a.AgentName == requestedBy, ct);
        if (agent is null)
        {
            return request; // unknown requester: no LLM, no MCP, no skills
        }

        var grants = ParseGrants(agent.McpGrantsJson);
        var servers = (await db.McpServers.AsNoTracking().ToListAsync(ct))
            .Select(ToRegistration)
            .ToList();
        var policy = new McpGrantPolicy(servers, grants);

        var (toolsJson, token) = await ResolveMcpToolsAsync(request.JobId, policy, grants, ct);
        var skills = await ResolveSkillsAsync(db, agent, policy, request.JobId, ct);

        return request with
        {
            LlmProvider = agent.LlmProvider,
            LlmModel = agent.LlmModel,
            McpToolsJson = toolsJson,
            McpGatewayToken = token,
            SkillInstructions = skills,
        };
    }

    private async Task<(string ToolsJson, string? Token)> ResolveMcpToolsAsync(
        string jobId, McpGrantPolicy policy, IReadOnlyList<McpGrant> grants, CancellationToken ct)
    {
        var effective = policy.EffectiveTools();
        if (effective.Count == 0)
        {
            return ("[]", null);
        }

        // Fetch live descriptors (schemas) per granted server; a server that is
        // down simply contributes no tools this run — fail soft, never open.
        var descriptors = new List<McpToolDescriptor>();
        foreach (var serverKey in effective.Select(e => e.ServerKey).Distinct())
        {
            try
            {
                var listed = await _mcp.ListToolsAsync(serverKey, ct);
                descriptors.AddRange(listed.Where(d => policy.ValidateTool(serverKey, d.Name).IsValid));
            }
            catch (Exception ex)
            {
                await _audit.WriteAsync(new DecisionAuditEvent
                {
                    JobId = jobId,
                    Actor = nameof(StoreSandboxJobEnricher),
                    Decision = "mcp-tools-list",
                    Allowed = false,
                    Reason = $"MCP server '{serverKey}' unavailable: {ex.Message}",
                }, ct);
            }
        }

        var toolsJson = JsonSerializer.Serialize(descriptors.Select(d => new
        {
            serverKey = d.ServerKey,
            name = d.Name,
            description = d.Description,
            inputSchemaJson = d.InputSchemaJson,
        }));

        var token = _tokens.Issue(jobId, grants);
        return (toolsJson, token);
    }

    private async Task<string?> ResolveSkillsAsync(
        DevAgentDbContext db, AgentSettingEntity agent, McpGrantPolicy policy, string jobId, CancellationToken ct)
    {
        var skillKeys = JsonColumns.ToList(agent.SkillKeysJson);
        if (skillKeys.Count == 0)
        {
            return null;
        }

        var effective = policy.EffectiveTools()
            .Select(e => $"mcp__{e.ServerKey}__{e.Tool}")
            .ToHashSet(StringComparer.Ordinal);

        var sb = new StringBuilder();
        foreach (var key in skillKeys)
        {
            var skill = await db.Skills.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key && s.Enabled, ct);
            if (skill is null)
            {
                continue;
            }

            // A skill never grants tools: if it requires something the agent
            // was not granted, the skill is REFUSED (audited), not widened.
            var missing = JsonColumns.ToList(skill.RequiredToolsJson)
                .Where(t => !BuiltinTools.Contains(t) && !effective.Contains(t))
                .ToList();
            if (missing.Count > 0)
            {
                await _audit.WriteAsync(new DecisionAuditEvent
                {
                    JobId = jobId,
                    Actor = nameof(StoreSandboxJobEnricher),
                    Decision = $"skill-{skill.Key}",
                    Allowed = false,
                    Reason = $"Skill requires ungranted tools: {string.Join(", ", missing)}.",
                }, ct);
                continue;
            }

            var instructions = await ResolveSkillTextAsync(skill, ct);
            if (string.IsNullOrWhiteSpace(instructions))
            {
                continue;
            }

            sb.AppendLine($"## Skill: {skill.Name}");
            sb.AppendLine(instructions.Trim());
            sb.AppendLine();

            // Skill text reaches the model — record it as prompt evidence.
            await _audit.WriteAsync(new PromptAuditEvent
            {
                JobId = jobId,
                Actor = $"skill:{skill.Key}",
                Prompt = instructions,
            }, ct);
        }

        return sb.Length == 0 ? null : sb.ToString().TrimEnd();
    }

    private async Task<string> ResolveSkillTextAsync(SkillEntity skill, CancellationToken ct)
    {
        // MCP-prompt-backed skill: prompts are fetched HOST-SIDE (here, on the
        // Runner) — the sandbox never speaks the prompts API.
        if (!string.IsNullOrWhiteSpace(skill.McpServerKey) && !string.IsNullOrWhiteSpace(skill.McpPromptName))
        {
            try
            {
                var prompt = await _mcp.GetPromptAsync(skill.McpServerKey!, skill.McpPromptName!, skill.McpPromptArgsJson, ct);
                return prompt.Text;
            }
            catch (Exception)
            {
                // Fall back to inline instructions when the server is down.
                return skill.Instructions;
            }
        }

        return skill.Instructions;
    }

    private static IReadOnlyList<McpGrant> ParseGrants(string json)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<List<GrantDto>>(json, JsonOptions) ?? new List<GrantDto>();
            return parsed
                .Where(g => !string.IsNullOrWhiteSpace(g.ServerKey))
                .Select(g => new McpGrant
                {
                    ServerKey = g.ServerKey!,
                    Tools = g.Tools ?? Array.Empty<string>(),
                    Prompts = g.Prompts ?? Array.Empty<string>(),
                })
                .ToList();
        }
        catch (JsonException)
        {
            return Array.Empty<McpGrant>();
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record GrantDto(string? ServerKey, string[]? Tools, string[]? Prompts);

    internal static McpServerRegistration ToRegistration(McpServerEntity entity) => new()
    {
        Key = entity.Key,
        Name = entity.Name,
        Endpoint = entity.Endpoint,
        AuthHeaderName = entity.AuthHeaderName,
        AuthTokenEnvVar = entity.AuthTokenEnvVar,
        AllowedTools = JsonColumns.ToList(entity.AllowedToolsJson),
        AllowedPrompts = JsonColumns.ToList(entity.AllowedPromptsJson),
        Enabled = entity.Enabled,
    };
}
