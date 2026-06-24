namespace DevAgent.Bridge.Llm;

using System.Text.Json;
using System.Text.Json.Nodes;
using DevAgent.Forge;

/// <summary>
/// The single source of truth that maps DevAgent.Forge's seven structured tools
/// to provider tool schemas, and maps a provider's chosen tool call back into a
/// strongly-typed <see cref="ToolCallRequest"/>.
///
/// SECURITY: This catalog is a CLOSED set. There is no shell / exec / network
/// tool here, and an unrecognised tool name maps to null (the agent loop treats
/// that as "no tool" and the model is asked again). Adding a tool is a
/// deliberate, reviewable code change.
/// </summary>
public static class LlmToolCatalog
{
    private sealed record Prop(string Name, string Type, string Description, bool Required);

    private sealed record ToolDef(string Name, string Description, Prop[] Props);

    private static readonly ToolDef[] Defs =
    {
        new("list_files", "List files and directories under a workspace-relative path.", new[]
        {
            new Prop("relativePath", "string", "Workspace-relative directory. Empty string means the workspace root.", false),
            new Prop("recursive", "boolean", "Recurse into sub-directories.", false),
        }),
        new("read_file", "Read a UTF-8 text file from the workspace.", new[]
        {
            new Prop("relativePath", "string", "Workspace-relative path of the file to read.", true),
        }),
        new("apply_patch", "Apply a unified-diff patch to a single workspace file.", new[]
        {
            new Prop("relativePath", "string", "Workspace-relative path of the file to patch.", true),
            new Prop("unifiedDiff", "string", "The unified-diff hunk(s) to apply to the file.", true),
        }),
        new("replace_file", "Replace the entire contents of a workspace file.", new[]
        {
            new Prop("relativePath", "string", "Workspace-relative path of the file to overwrite.", true),
            new Prop("newContent", "string", "The complete new file contents.", true),
        }),
        new("run_dotnet_build", "Run 'dotnet build' on a workspace project or solution.", new[]
        {
            new Prop("projectOrSolution", "string", "Workspace-relative project/solution dir. Empty string means the workspace root.", false),
        }),
        new("run_dotnet_test", "Run 'dotnet test' on a workspace project or solution.", new[]
        {
            new Prop("projectOrSolution", "string", "Workspace-relative project/solution dir. Empty string means the workspace root.", false),
        }),
        new("git_status", "Show 'git status' for the workspace (read-only).", Array.Empty<Prop>()),
    };

    /// <summary>The stable wire names of every tool the agent may call.</summary>
    public static IReadOnlyList<string> ToolNames { get; } = Array.ConvertAll(Defs, d => d.Name);

    /// <summary>JSON-schema "input_schema" object for a tool (fresh node each call).</summary>
    private static JsonObject BuildSchema(ToolDef def, bool includeAdditionalProperties)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var p in def.Props)
        {
            properties[p.Name] = new JsonObject
            {
                ["type"] = p.Type,
                ["description"] = p.Description,
            };
            if (p.Required)
            {
                required.Add(p.Name);
            }
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
        };

        // Gemini's function-declaration schema subset rejects additionalProperties.
        if (includeAdditionalProperties)
        {
            schema["additionalProperties"] = false;
        }

        return schema;
    }

    /// <summary>Anthropic "tools": [{ name, description, input_schema }].</summary>
    public static JsonArray AnthropicTools()
    {
        var arr = new JsonArray();
        foreach (var def in Defs)
        {
            arr.Add(new JsonObject
            {
                ["name"] = def.Name,
                ["description"] = def.Description,
                ["input_schema"] = BuildSchema(def, includeAdditionalProperties: true),
            });
        }

        return arr;
    }

    /// <summary>OpenAI "tools": [{ type:function, function:{ name, description, parameters } }].</summary>
    public static JsonArray OpenAiTools()
    {
        var arr = new JsonArray();
        foreach (var def in Defs)
        {
            arr.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = def.Name,
                    ["description"] = def.Description,
                    ["parameters"] = BuildSchema(def, includeAdditionalProperties: true),
                },
            });
        }

        return arr;
    }

    /// <summary>Gemini "tools": [{ function_declarations: [{ name, description, parameters }] }].</summary>
    public static JsonArray GeminiTools()
    {
        var declarations = new JsonArray();
        foreach (var def in Defs)
        {
            declarations.Add(new JsonObject
            {
                ["name"] = def.Name,
                ["description"] = def.Description,
                ["parameters"] = BuildSchema(def, includeAdditionalProperties: false),
            });
        }

        return new JsonArray { new JsonObject { ["function_declarations"] = declarations } };
    }

    /// <summary>Serialise a tool call's arguments back to a JSON object (for history replay).</summary>
    public static JsonObject Arguments(ToolCallRequest request) => request switch
    {
        ListFilesToolCall c => new JsonObject { ["relativePath"] = c.RelativePath, ["recursive"] = c.Recursive },
        ReadFileToolCall c => new JsonObject { ["relativePath"] = c.RelativePath },
        ApplyPatchToolCall c => new JsonObject { ["relativePath"] = c.RelativePath, ["unifiedDiff"] = c.UnifiedDiff },
        ReplaceFileToolCall c => new JsonObject { ["relativePath"] = c.RelativePath, ["newContent"] = c.NewContent },
        RunBuildToolCall c => new JsonObject { ["projectOrSolution"] = c.ProjectOrSolution },
        RunTestToolCall c => new JsonObject { ["projectOrSolution"] = c.ProjectOrSolution },
        GitStatusToolCall => new JsonObject(),
        _ => new JsonObject(),
    };

    /// <summary>
    /// Map a provider's chosen tool (name + arguments) into a typed request.
    /// Returns null for an unknown tool name. The provider's tool-call id, when
    /// supplied, is preserved as the <see cref="ToolCallRequest.ToolCallId"/> so
    /// the conversation history round-trips cleanly.
    /// </summary>
    public static ToolCallRequest? Parse(string toolName, JsonObject? args, string? toolCallId = null)
    {
        args ??= new JsonObject();

        ToolCallRequest? request = toolName switch
        {
            "list_files" => new ListFilesToolCall { RelativePath = Str(args, "relativePath"), Recursive = Bool(args, "recursive") },
            "read_file" => new ReadFileToolCall { RelativePath = Str(args, "relativePath") },
            "apply_patch" => new ApplyPatchToolCall { RelativePath = Str(args, "relativePath"), UnifiedDiff = Str(args, "unifiedDiff") },
            "replace_file" => new ReplaceFileToolCall { RelativePath = Str(args, "relativePath"), NewContent = Str(args, "newContent") },
            "run_dotnet_build" => new RunBuildToolCall { ProjectOrSolution = Str(args, "projectOrSolution") },
            "run_dotnet_test" => new RunTestToolCall { ProjectOrSolution = Str(args, "projectOrSolution") },
            "git_status" => new GitStatusToolCall(),
            _ => null,
        };

        if (request is not null && !string.IsNullOrEmpty(toolCallId))
        {
            request = request with { ToolCallId = toolCallId! };
        }

        return request;
    }

    private static string Str(JsonObject args, string key, string fallback = "")
    {
        var node = args[key];
        if (node is null)
        {
            return fallback;
        }

        return node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToJsonString();
    }

    private static bool Bool(JsonObject args, string key)
    {
        var node = args[key];
        return node is not null
            && node.GetValueKind() == JsonValueKind.True;
    }
}
