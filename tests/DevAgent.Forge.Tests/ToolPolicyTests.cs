namespace DevAgent.Forge.Tests;

using DevAgent.Forge.Tools;
using Xunit;

public class ToolPolicyTests
{
    private readonly ToolPolicy _policy = new();

    [Theory]
    [InlineData("list_files")]
    [InlineData("read_file")]
    [InlineData("apply_patch")]
    [InlineData("replace_file")]
    [InlineData("run_dotnet_build")]
    [InlineData("run_dotnet_test")]
    [InlineData("git_status")]
    public void Allowed_tools_pass(string tool)
    {
        Assert.True(_policy.IsAllowed(tool));
    }

    [Theory]
    [InlineData("bash")]
    [InlineData("sh")]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [InlineData("cmd")]
    [InlineData("curl")]
    [InlineData("wget")]
    [InlineData("ssh")]
    [InlineData("docker")]
    [InlineData("kubectl")]
    [InlineData("az")]
    [InlineData("aws")]
    [InlineData("run_command")]
    [InlineData("exec")]
    [InlineData("eval")]
    public void Dangerous_tools_are_rejected(string tool)
    {
        Assert.False(_policy.IsAllowed(tool));
        Assert.False(_policy.Validate(tool).IsValid);
    }

    [Fact]
    public void Unknown_tools_are_rejected()
    {
        Assert.False(_policy.IsAllowed("teleport"));
        Assert.False(_policy.IsAllowed(""));
    }
}
