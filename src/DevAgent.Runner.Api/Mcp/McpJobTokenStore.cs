namespace DevAgent.Runner.Api.Mcp;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using DevAgent.Bridge.Mcp;

/// <summary>The MCP access minted for one sandbox job.</summary>
public sealed record McpJobAccess(string JobId, IReadOnlyList<McpGrant> Grants);

/// <summary>
/// Issues and resolves short-lived, per-job bearer tokens for the MCP gateway.
///
/// SECURITY: The token is the ONLY credential a sandbox holds. It is random
/// (256-bit), scoped to one job's grants, expires, and grants nothing but the
/// right to ask the gateway — which still re-validates every call against the
/// registry ∩ grants. MCP server credentials never leave the gateway host.
/// </summary>
public interface IMcpJobTokenStore
{
    string Issue(string jobId, IReadOnlyList<McpGrant> grants);
    McpJobAccess? Resolve(string token);
    void Revoke(string jobId);
}

public sealed class InMemoryMcpJobTokenStore : IMcpJobTokenStore
{
    private sealed record Entry(McpJobAccess Access, DateTimeOffset ExpiresUtc);

    private readonly ConcurrentDictionary<string, Entry> _byToken = new();
    private readonly TimeSpan _lifetime;

    public InMemoryMcpJobTokenStore(TimeSpan? lifetime = null)
    {
        _lifetime = lifetime ?? TimeSpan.FromHours(2);
    }

    public string Issue(string jobId, IReadOnlyList<McpGrant> grants)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        _byToken[token] = new Entry(new McpJobAccess(jobId, grants), DateTimeOffset.UtcNow + _lifetime);
        return token;
    }

    public McpJobAccess? Resolve(string token)
    {
        if (string.IsNullOrEmpty(token) || !_byToken.TryGetValue(token, out var entry))
        {
            return null;
        }

        if (entry.ExpiresUtc < DateTimeOffset.UtcNow)
        {
            _byToken.TryRemove(token, out _);
            return null;
        }

        return entry.Access;
    }

    public void Revoke(string jobId)
    {
        foreach (var pair in _byToken.Where(p => p.Value.Access.JobId == jobId).ToList())
        {
            _byToken.TryRemove(pair.Key, out _);
        }
    }
}
