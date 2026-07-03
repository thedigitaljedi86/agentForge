namespace DevAgent.Bridge.Llm.Tests;

using System.Text.Json.Nodes;
using DevAgent.Bridge.Llm;
using DevAgent.Forge;
using Xunit;

/// <summary>
/// The MCP extension point in the LLM catalog: granted tools appear in every
/// provider's schema under the mcp__ namespace, and calls parse back into the
/// single typed McpToolCall. The built-in set stays closed.
/// </summary>
public class McpDynamicToolTests
{
    private static readonly LlmToolDescriptor Extra = new(
        "mcp__advisories__query",
        "Query security advisories for a NuGet package.",
        """{"type":"object","properties":{"package":{"type":"string"}},"required":["package"]}""");

    [Fact]
    public void Extras_appear_in_all_three_provider_schemas()
    {
        var extras = new[] { Extra };

        var anthropic = LlmToolCatalog.AnthropicTools(extras);
        Assert.Contains(anthropic, t => (string?)t?["name"] == Extra.Name);

        var openai = LlmToolCatalog.OpenAiTools(extras);
        Assert.Contains(openai, t => (string?)t?["function"]?["name"] == Extra.Name);

        var gemini = LlmToolCatalog.GeminiTools(extras);
        var declarations = gemini[0]!["function_declarations"]!.AsArray();
        Assert.Contains(declarations, t => (string?)t?["name"] == Extra.Name);
    }

    [Fact]
    public void Extras_carry_the_servers_input_schema_verbatim()
    {
        var anthropic = LlmToolCatalog.AnthropicTools(new[] { Extra });
        var tool = anthropic.First(t => (string?)t?["name"] == Extra.Name)!;

        Assert.Equal("string", (string?)tool["input_schema"]!["properties"]!["package"]!["type"]);
    }

    [Fact]
    public void Without_extras_the_catalog_is_unchanged()
    {
        Assert.Equal(7, LlmToolCatalog.AnthropicTools().Count);
        Assert.Equal(7, LlmToolCatalog.OpenAiTools().Count);
    }

    [Fact]
    public void Mcp_wire_name_parses_into_a_typed_mcp_call()
    {
        var args = new JsonObject { ["package"] = "Serilog" };

        var parsed = LlmToolCatalog.Parse("mcp__advisories__query", args, "call-9");

        var mcp = Assert.IsType<McpToolCall>(parsed);
        Assert.Equal("advisories", mcp.ServerKey);
        Assert.Equal("query", mcp.Tool);
        Assert.Contains("Serilog", mcp.ArgumentsJson);
        Assert.Equal("call-9", mcp.ToolCallId);
    }

    [Fact]
    public void Tool_names_with_underscores_split_on_the_first_separator()
    {
        var parsed = LlmToolCatalog.Parse("mcp__srv__query__deep", new JsonObject());

        var mcp = Assert.IsType<McpToolCall>(parsed);
        Assert.Equal("srv", mcp.ServerKey);
        Assert.Equal("query__deep", mcp.Tool); // tool may contain "__"; server key may not
    }

    [Fact]
    public void Malformed_mcp_names_parse_to_null()
    {
        Assert.Null(LlmToolCatalog.Parse("mcp__", new JsonObject()));
        Assert.Null(LlmToolCatalog.Parse("mcp__onlyserver", new JsonObject()));
        Assert.Null(LlmToolCatalog.Parse("mcp____", new JsonObject()));
    }

    [Fact]
    public void Mcp_arguments_round_trip_for_history_replay()
    {
        var call = new McpToolCall
        {
            ServerKey = "advisories",
            Tool = "query",
            ArgumentsJson = """{"package":"Serilog","includePrerelease":false}""",
        };

        var replay = LlmToolCatalog.Arguments(call);

        Assert.Equal("Serilog", (string?)replay["package"]);
    }

    [Fact]
    public void Skill_instructions_are_rendered_into_the_system_prompt()
    {
        var task = new CodingAgentTask
        {
            JobId = "j",
            Goal = "Fix the build.",
            WorkspaceRoot = "/workspace/repo",
            SkillInstructions = "Always check security advisories before bumping a version.",
        };

        var prompt = LlmConversationProbe.SystemPrompt(task);

        Assert.Contains("Always check security advisories", prompt);
        Assert.Contains("grant no extra permissions", prompt);

        var without = LlmConversationProbe.SystemPrompt(task with { SkillInstructions = null });
        Assert.DoesNotContain("Skill instructions", without);
    }
}

/// <summary>LlmConversation is internal; probe it via InternalsVisibleTo-free reflection.</summary>
internal static class LlmConversationProbe
{
    public static string SystemPrompt(CodingAgentTask task)
    {
        var type = typeof(LlmToolCatalog).Assembly.GetType("DevAgent.Bridge.Llm.LlmConversation")!;
        var method = type.GetMethod("SystemPrompt")!;
        return (string)method.Invoke(null, new object[] { task })!;
    }
}
