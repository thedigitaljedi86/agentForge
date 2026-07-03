namespace DevAgent.Bridge.Llm.Tests;

using System.Text.Json.Nodes;
using DevAgent.Bridge.Llm;
using DevAgent.Forge;
using Xunit;

public class LlmToolCatalogTests
{
    [Fact]
    public void Every_forge_tool_is_exposed()
    {
        Assert.Equal(new[]
        {
            "list_files", "read_file", "apply_patch", "replace_file",
            "run_dotnet_build", "run_dotnet_test", "git_status",
        }, LlmToolCatalog.ToolNames);
    }

    [Fact]
    public void Arguments_round_trip_back_into_typed_requests()
    {
        var patch = new ApplyPatchToolCall { RelativePath = "src/A.cs", UnifiedDiff = "@@ -1 +1 @@\n-a\n+b\n" };
        var args = LlmToolCatalog.Arguments(patch);

        var parsed = LlmToolCatalog.Parse("apply_patch", args, toolCallId: "call-42");

        var typed = Assert.IsType<ApplyPatchToolCall>(parsed);
        Assert.Equal("src/A.cs", typed.RelativePath);
        Assert.Equal(patch.UnifiedDiff, typed.UnifiedDiff);
        Assert.Equal("call-42", typed.ToolCallId); // provider id is preserved for history replay
    }

    [Fact]
    public void List_files_parses_string_and_bool_arguments()
    {
        var args = new JsonObject { ["relativePath"] = "src", ["recursive"] = true };
        var parsed = Assert.IsType<ListFilesToolCall>(LlmToolCatalog.Parse("list_files", args));
        Assert.Equal("src", parsed.RelativePath);
        Assert.True(parsed.Recursive);
    }

    [Fact]
    public void Unknown_tool_name_maps_to_null()
    {
        Assert.Null(LlmToolCatalog.Parse("bash", new JsonObject { ["cmd"] = "rm -rf /" }));
        Assert.Null(LlmToolCatalog.Parse("run_command", null));
    }

    [Fact]
    public void Anthropic_schema_lists_all_tools_with_input_schema()
    {
        var tools = LlmToolCatalog.AnthropicTools();
        Assert.Equal(7, tools.Count);
        foreach (var tool in tools)
        {
            var obj = Assert.IsType<JsonObject>(tool);
            Assert.NotNull(obj["name"]);
            Assert.NotNull(obj["input_schema"]);
        }
    }

    [Fact]
    public void Openai_schema_wraps_each_tool_as_a_function()
    {
        var tools = LlmToolCatalog.OpenAiTools();
        Assert.Equal(7, tools.Count);
        var first = Assert.IsType<JsonObject>(tools[0]);
        Assert.Equal("function", first["type"]!.GetValue<string>());
        Assert.NotNull((first["function"] as JsonObject)?["parameters"]);
    }

    [Fact]
    public void Gemini_schema_omits_additionalProperties()
    {
        var tools = LlmToolCatalog.GeminiTools();
        var declarations = (tools[0] as JsonObject)!["function_declarations"] as JsonArray;
        Assert.Equal(7, declarations!.Count);
        foreach (var decl in declarations)
        {
            var parameters = (decl as JsonObject)!["parameters"] as JsonObject;
            Assert.False(parameters!.ContainsKey("additionalProperties"));
        }
    }
}
