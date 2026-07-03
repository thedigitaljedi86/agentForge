namespace DevAgent.Worker.DotNet;

using System.Net;
using DevAgent.Bridge.Mcp;
using DevAgent.Forge;
using DevAgent.Forge.Tools;

/// <summary>
/// The worker-side <see cref="IMcpToolExecutor"/>: forwards a validated tool
/// call to the Runner's MCP gateway using the job's short-lived token.
///
/// SECURITY: The sandbox holds no MCP server endpoint and no credential — only
/// the gateway URL and a per-job token. The gateway re-validates every call
/// against the registry ∩ this job's grants; a 401/403 from it surfaces here
/// as a policy denial the model can see (and the audit trail records twice:
/// once here, once at the gateway).
/// </summary>
public sealed class McpGatewayToolExecutor : IMcpToolExecutor
{
    private readonly IMcpClient _gateway;

    public McpGatewayToolExecutor(IMcpClient gateway)
    {
        _gateway = gateway;
    }

    public async Task<ToolCallResult> ExecuteAsync(McpToolCall call, CancellationToken cancellationToken = default)
    {
        // Defence in depth: shell-like tool names are refused by name alone,
        // even if an administrator allowlisted them by mistake.
        if (ToolPolicy.IsForbiddenName(call.Tool))
        {
            return ToolCallResult.Denied(call, $"MCP tool name '{call.Tool}' is forbidden.");
        }

        try
        {
            var result = await _gateway.CallToolAsync(call.ServerKey, call.Tool, call.ArgumentsJson, cancellationToken);
            return result.Success
                ? ToolCallResult.Ok(call, result.Content)
                : ToolCallResult.Fail(call, result.Error ?? "The MCP tool reported an error.");
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return ToolCallResult.Denied(call, "The MCP gateway rejected this call (no grant for this server/tool).");
        }
        catch (Exception ex)
        {
            return ToolCallResult.Fail(call, $"MCP gateway unreachable: {ex.Message}");
        }
    }
}
