namespace DevAgent.Guard.Tests;

using DevAgent.Contracts.Validation;
using DevAgent.Guard.Paths;
using Xunit;

public class WorkspacePathValidatorTests
{
    private static WorkspacePathValidator NewValidator() =>
        new(Path.Combine(Path.GetTempPath(), "devagent-workspace"));

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("repo/../../escape")]
    [InlineData("a/b/../../../outside")]
    public void Path_traversal_is_rejected(string relative)
    {
        var validator = NewValidator();
        Assert.False(validator.Validate(relative).IsValid);
        Assert.Throws<PolicyViolationException>(() => validator.ResolveInsideWorkspace(relative));
    }

    [Theory]
    [InlineData("/etc/shadow")]
    [InlineData("/usr/bin/whatever")]
    public void Absolute_paths_are_rejected(string absolute)
    {
        var validator = NewValidator();
        Assert.False(validator.Validate(absolute).IsValid);
    }

    [Theory]
    [InlineData("repo")]
    [InlineData("repo/src/Project.csproj")]
    [InlineData("a/b/c.txt")]
    public void Paths_inside_workspace_are_allowed(string relative)
    {
        var validator = NewValidator();
        Assert.True(validator.Validate(relative).IsValid);
        var resolved = validator.ResolveInsideWorkspace(relative);
        Assert.StartsWith(validator.WorkspaceRoot, resolved);
    }

    [Fact]
    public void Sibling_directory_with_shared_prefix_is_rejected()
    {
        // Guards against "/work" matching "/workspace".
        var validator = new WorkspacePathValidator("/tmp/work");
        Assert.False(validator.Validate("../workspace/secret").IsValid);
    }
}
