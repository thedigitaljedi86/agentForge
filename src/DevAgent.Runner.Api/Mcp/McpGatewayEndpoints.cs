namespace DevAgent.Runner.Api.Mcp;

using DevAgent.Audit;
using DevAgent.Bridge.Mcp;
using DevAgent.Store;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The MCP GATEWAY: the only path from a sandbox to any MCP server.
///
/// SECURITY:
///   * Every request needs a valid per-job bearer token (256-bit, expiring).
///   * Every call is re-validated against the registry ∩ the job's grants —
///     the sandbox's own claims are never trusted (defence in depth).
///   * Server credentials live in the gateway host's environment only.
///   * Every tool call and prompt fetch is audited with the job id.
/// </summary>
public static class McpGatewayEndpoints
{
    public sealed record ToolListBody(string ServerKey);
    public sealed record ToolCallBody(string ServerKey, string Tool, string ArgumentsJson);
    public sealed record PromptListBody(string ServerKey);
    public sealed record PromptGetBody(string ServerKey, string PromptName, string ArgumentsJson);

    public static void MapMcpGateway(this WebApplication app)
    {
        app.MapPost("/mcp/tools/list", async (
            HttpContext http, ToolListBody body,
            IMcpJobTokenStore tokens, IDbContextFactory<DevAgentDbContext> dbFactory,
            IMcpClient mcp, CancellationToken ct) =>
        {
            var (access, error) = Authenticate(http, tokens);
            if (access is null)
            {
                return error!;
            }

            var policy = await PolicyForAsync(dbFactory, access, ct);
            var tools = await mcp.ListToolsAsync(body.ServerKey, ct);
            return Results.Ok(tools.Where(t => policy.ValidateTool(body.ServerKey, t.Name).IsValid).ToList());
        });

        app.MapPost("/mcp/tools/call", async (
            HttpContext http, ToolCallBody body,
            IMcpJobTokenStore tokens, IDbContextFactory<DevAgentDbContext> dbFactory,
            IMcpClient mcp, IAuditLog audit, CancellationToken ct) =>
        {
            var (access, error) = Authenticate(http, tokens);
            if (access is null)
            {
                return error!;
            }

            var policy = await PolicyForAsync(dbFactory, access, ct);
            var check = policy.ValidateTool(body.ServerKey, body.Tool);

            await audit.WriteAsync(new ToolCallAuditEvent
            {
                JobId = access.JobId,
                Actor = "McpGateway",
                ToolName = $"mcp__{body.ServerKey}__{body.Tool}",
                Arguments = $"argBytes={body.ArgumentsJson?.Length ?? 0}",
                Allowed = check.IsValid,
                DenyReason = check.IsValid ? null : check.Reason,
            }, ct);

            if (!check.IsValid)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var result = await mcp.CallToolAsync(body.ServerKey, body.Tool, body.ArgumentsJson ?? "{}", ct);
            return Results.Ok(result);
        });

        app.MapPost("/mcp/prompts/list", async (
            HttpContext http, PromptListBody body,
            IMcpJobTokenStore tokens, IDbContextFactory<DevAgentDbContext> dbFactory,
            IMcpClient mcp, CancellationToken ct) =>
        {
            var (access, error) = Authenticate(http, tokens);
            if (access is null)
            {
                return error!;
            }

            var policy = await PolicyForAsync(dbFactory, access, ct);
            var prompts = await mcp.ListPromptsAsync(body.ServerKey, ct);
            return Results.Ok(prompts.Where(p => policy.ValidatePrompt(body.ServerKey, p.Name).IsValid).ToList());
        });

        app.MapPost("/mcp/prompts/get", async (
            HttpContext http, PromptGetBody body,
            IMcpJobTokenStore tokens, IDbContextFactory<DevAgentDbContext> dbFactory,
            IMcpClient mcp, IAuditLog audit, CancellationToken ct) =>
        {
            var (access, error) = Authenticate(http, tokens);
            if (access is null)
            {
                return error!;
            }

            var policy = await PolicyForAsync(dbFactory, access, ct);
            var check = policy.ValidatePrompt(body.ServerKey, body.PromptName);

            await audit.WriteAsync(new ToolCallAuditEvent
            {
                JobId = access.JobId,
                Actor = "McpGateway",
                ToolName = $"mcp-prompt__{body.ServerKey}__{body.PromptName}",
                Arguments = $"argBytes={body.ArgumentsJson?.Length ?? 0}",
                Allowed = check.IsValid,
                DenyReason = check.IsValid ? null : check.Reason,
            }, ct);

            if (!check.IsValid)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var prompt = await mcp.GetPromptAsync(body.ServerKey, body.PromptName, body.ArgumentsJson ?? "{}", ct);
            return Results.Ok(prompt);
        });
    }

    private static (McpJobAccess? Access, IResult? Error) Authenticate(HttpContext http, IMcpJobTokenStore tokens)
    {
        var header = http.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.Ordinal))
        {
            return (null, Results.Unauthorized());
        }

        var access = tokens.Resolve(header[prefix.Length..].Trim());
        return access is null ? (null, Results.Unauthorized()) : (access, null);
    }

    private static async Task<McpGrantPolicy> PolicyForAsync(
        IDbContextFactory<DevAgentDbContext> dbFactory, McpJobAccess access, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var servers = (await db.McpServers.AsNoTracking().ToListAsync(ct))
            .Select(StoreSandboxJobEnricher.ToRegistration);
        return new McpGrantPolicy(servers, access.Grants);
    }
}
