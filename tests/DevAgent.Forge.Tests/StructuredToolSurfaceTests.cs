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

    [Fact]
    public void No_shell_or_exec_tool_type_exists()
    {
        foreach (var type in ToolCallTypes())
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
        var toolNames = ToolCallTypes()
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
    public void Tool_policy_allowlist_matches_the_tool_types()
    {
        var policy = new ToolPolicy();
        foreach (var type in ToolCallTypes())
        {
            var name = (string)type.GetProperty(nameof(ToolCallRequest.ToolName))!
                .GetValue(System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type))!;
            Assert.True(policy.IsAllowed(name), $"Tool '{name}' should be allowed by ToolPolicy.");
        }
    }
}
