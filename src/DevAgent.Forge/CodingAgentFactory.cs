namespace DevAgent.Forge;

using DevAgent.Audit;
using DevAgent.Forge.Tools;
using DevAgent.Guard.Execution;
using DevAgent.Guard.Paths;
using DevAgent.Guard.Policies;

/// <summary>
/// Wires up a fully policy-enforced <see cref="CodingAgent"/> for a specific
/// workspace. This is the single composition point so that callers (the worker,
/// tests) cannot accidentally assemble the agent with a missing guard.
///
/// SECURITY: All tools share ONE <see cref="WorkspacePathValidator"/> rooted at
/// the repository workspace and ONE <see cref="SafeCommandRunner"/> limited to
/// dotnet/git. There is no path by which a tool can reach outside the workspace
/// or run a non-allowlisted command.
/// </summary>
public static class CodingAgentFactory
{
    public static ICodingAgent Create(
        string workspaceRoot,
        ILlmClient llm,
        IAuditLog audit,
        string jobId,
        CodingAgentOptions? options = null,
        IProcessExecutor? processExecutor = null,
        ProtectedFilePolicy? protectedFiles = null,
        ToolPolicy? toolPolicy = null)
    {
        options ??= new CodingAgentOptions();
        protectedFiles ??= new ProtectedFilePolicy();
        toolPolicy ??= new ToolPolicy();

        var paths = new WorkspacePathValidator(workspaceRoot);
        var commandRunner = new SafeCommandRunner(new CommandPolicy(), paths, processExecutor);

        var fileTool = new WorkspaceFileTool(paths, protectedFiles, options.AllowDeploymentFileEdits);
        var patchService = new PatchApplicationService(paths, protectedFiles, options.AllowDeploymentFileEdits);
        var commandTools = new DotNetCommandTools(commandRunner);

        var handler = new CodingAgentToolHandler(toolPolicy, fileTool, patchService, commandTools, audit, jobId);

        return new CodingAgent(llm, handler, audit, options);
    }
}
