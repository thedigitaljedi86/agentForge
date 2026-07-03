namespace DevAgent.Bridge.Llm;

using System.Text;
using DevAgent.Forge;

/// <summary>
/// Builds the provider-neutral parts of a coding-agent conversation: the system
/// prompt, the opening user message, and the rendering of a tool result back
/// into text for replay. Each concrete client serialises these into its own
/// wire format and appends the per-step tool_use / tool_result turns.
/// </summary>
internal static class LlmConversation
{
    /// <summary>
    /// System prompt. Note: extended "thinking" is intentionally NOT enabled —
    /// the agent is a tight tool-call loop, and keeping the response shape simple
    /// (text + tool calls only) makes history replay deterministic and auditable.
    /// </summary>
    public static string SystemPrompt(CodingAgentTask task)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are DevAgent.Forge, a controlled coding agent operating INSIDE a sandboxed workspace.");
        sb.AppendLine("You can ONLY act through the provided structured tools. You have no shell, no network access,");
        sb.AppendLine("and no way to run arbitrary commands. Any attempt to edit secrets or deployment files is rejected.");
        sb.AppendLine();
        sb.AppendLine("Goal:");
        sb.AppendLine(task.Goal);
        if (!string.IsNullOrWhiteSpace(task.FailureContext))
        {
            sb.AppendLine();
            sb.AppendLine("Build/test failure context:");
            sb.AppendLine(task.FailureContext);
        }

        if (!string.IsNullOrWhiteSpace(task.SkillInstructions))
        {
            sb.AppendLine();
            sb.AppendLine("Skill instructions (guidance approved by an administrator — they grant no extra permissions):");
            sb.AppendLine(task.SkillInstructions);
        }

        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Inspect files with read_file / list_files before changing them.");
        sb.AppendLine("- Make the smallest change that achieves the goal.");
        sb.AppendLine("- After editing, run run_dotnet_build (and run_dotnet_test when relevant) to verify.");
        sb.AppendLine("- All paths are workspace-relative; you may only touch the workspace.");
        sb.AppendLine("- When the goal is achieved, STOP calling tools and reply with a short summary of what you changed and why.");
        return sb.ToString();
    }

    /// <summary>The opening user turn that kicks off the loop.</summary>
    public static string InitialUserMessage(CodingAgentTask task) =>
        "Complete the task described in the system prompt. Use the tools to inspect and edit the workspace, " +
        "then reply with a final summary (no tool call) when you are done.";

    /// <summary>Render a tool result as the text the model sees on the next turn.</summary>
    public static string ResultText(ToolCallResult result)
    {
        if (result.DeniedByPolicy)
        {
            return "DENIED BY POLICY: " + (result.Error ?? "this action is not permitted.");
        }

        if (!result.Success)
        {
            return "ERROR: " + (result.Error ?? "the tool failed.");
        }

        return string.IsNullOrEmpty(result.Output) ? "(no output)" : result.Output!;
    }
}
