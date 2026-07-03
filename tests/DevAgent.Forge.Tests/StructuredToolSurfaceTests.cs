namespace DevAgent.Forge.Tests;

using DevAgent.Forge;
using DevAgent.Forge.Tools;
using Xunit;

/// <summary>
/// These tests lock in the rule that the LLM can only ever act through explicit,
/// structured tools — there is no shell, no "exec", and no generic command tool
/// now or by accident later.
/// </summary>
public class StructuredToolSurfaceTests
{
    private static IReadOnlyList<Type> ToolCallTypes() =>
        typeof(ToolCallRequest).Assembly.GetTypes()
            .Where(t => typeof(ToolCallRequest).IsAssignableFrom(t) && !t.IsAbstract)
            .ToList();

    // McpToolCall is the single, deliberate dynamic doorway; it is gated by the
    // MCP registry ∩ grant policy instead of ToolPolicy, so the closed-set
    // assertions below exclude it and it gets its own tests.
    private static IReadOnlyList<Type> BuiltinToolCallTypes() =>
        ToolCallTypes().Where(t => t != typeof(McpToolCall)).ToList();

    [Fact]
    public void No_shell_or_exec_tool_type_exists()
    {
        foreach (var type in BuiltinToolCallTypes())
        {
            var instance = (ToolCallRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
            var toolName = (string)type.GetProperty(nameof(ToolCallRequest.ToolName))!.GetValue(instance)!;

            foreach (var banned in new[] { "shell", "exec", "bash", "cmd", "command" })
            {
                Assert.DoesNotContain(banned, toolName, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Tool_surface_is_exactly_the_seven_allowed_tools()
    {
        var toolNames = BuiltinToolCallTypes()
            .Select(t => (string)t.GetProperty(nameof(ToolCallRequest.ToolName))!
                .GetValue(System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t))!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "apply_patch",
                "git_status",
                "list_files",
                "read_file",
                "replace_file",
                "run_dotnet_build",
                "run_dotnet_test",
            },
            toolNames);
    }

    [Fact]
    public void The_only_dynamic_tool_type_is_the_gated_mcp_call()
    {
        var dynamicTypes = ToolCallTypes().Except(BuiltinToolCallTypes()).ToList();
        Assert.Equal(typeof(McpToolCall), Assert.Single(dynamicTypes));

        // Its wire name is namespaced and cannot collide with builtin names.
        var call = new McpToolCall { ServerKey = "srv", Tool = "query" };
        Assert.Equal("mcp__srv__query", call.ToolName);

        // ToolPolicy does NOT allow it — the grant policy is its gate, and the
        // handler denies it outright when no MCP executor is configured.
        Assert.False(new ToolPolicy().IsAllowed(call.ToolName));
    }

    [Fact]
    public void Tool_policy_allowlist_matches_the_tool_types()
    {
        var policy = new ToolPolicy();
        foreach (var type in BuiltinToolCallTypes())
        {
            var name = (string)type.GetProperty(nameof(ToolCallRequest.ToolName))!
                .GetValue(System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type))!;
            Assert.True(policy.IsAllowed(name), $"Tool '{name}' should be allowed by ToolPolicy.");
        }
    }
}
