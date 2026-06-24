namespace DevAgent.Forge.Tests;

using System.Reflection;
using DevAgent.Forge;
using Xunit;

/// <summary>
/// Forge is the future LLM layer. These tests lock in the rule that the LLM can
/// only ever act through explicit, structured tools — there is no shell, no
/// "exec", and no generic command tool now or by accident later.
/// </summary>
public class StructuredToolSurfaceTests
{
    private static IReadOnlyList<Type> ToolCallTypes() =>
        typeof(ToolCallRequest).Assembly.GetTypes()
            .Where(t => typeof(ToolCallRequest).IsAssignableFrom(t) && !t.IsAbstract)
            .ToList();

    [Fact]
    public void No_shell_or_exec_tool_exists()
    {
        foreach (var type in ToolCallTypes())
        {
            var name = type.Name.ToLowerInvariant();
            Assert.DoesNotContain("shell", name);
            Assert.DoesNotContain("exec", name);

            // Also check the tool's declared ToolName value.
            var instance = (ToolCallRequest)FormatterServicesCreate(type);
            var toolName = type.GetProperty(nameof(ToolCallRequest.ToolName))!.GetValue(instance) as string ?? "";
            Assert.DoesNotContain("shell", toolName, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("exec", toolName, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Tool_surface_is_limited_to_known_structured_tools()
    {
        var names = ToolCallTypes().Select(t => t.Name).OrderBy(n => n).ToArray();

        Assert.Equal(
            new[]
            {
                nameof(ApplyPatchToolCall),
                nameof(ReadFileToolCall),
                nameof(RunBuildToolCall),
                nameof(RunTestToolCall),
            },
            names);
    }

    // Creates an instance without invoking constructors that require members,
    // sufficient for reading the constant ToolName.
    private static object FormatterServicesCreate(Type type) =>
        System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
}
