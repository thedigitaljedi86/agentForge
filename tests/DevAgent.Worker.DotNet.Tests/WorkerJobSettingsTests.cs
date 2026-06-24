namespace DevAgent.Worker.DotNet.Tests;

using DevAgent.Worker.DotNet;
using Xunit;

public class WorkerJobSettingsTests
{
    private static readonly Dictionary<string, string?> Complete = new()
    {
        [WorkerJobSettings.JobIdVar] = "job-1",
        [WorkerJobSettings.CloneUrlVar] = "https://git/x.git",
        [WorkerJobSettings.BaseBranchVar] = "main",
        [WorkerJobSettings.PackageIdVar] = "Serilog",
        [WorkerJobSettings.TargetVersionVar] = "3.1.1",
        [WorkerJobSettings.WorkspaceRootVar] = "/workspace",
        [WorkerJobSettings.GitTokenVar] = "bot-token",
    };

    [Fact]
    public void Worker_parses_complete_environment()
    {
        var settings = WorkerJobSettings.FromEnvironment(name => Complete.GetValueOrDefault(name));

        Assert.Equal("job-1", settings.JobId);
        Assert.Equal("Serilog", settings.PackageId);
        Assert.Equal("/workspace", settings.WorkspaceRoot);
        Assert.True(settings.OnlyUpgrade); // default when unset
    }

    [Theory]
    [InlineData(WorkerJobSettings.JobIdVar)]
    [InlineData(WorkerJobSettings.CloneUrlVar)]
    [InlineData(WorkerJobSettings.BaseBranchVar)]
    [InlineData(WorkerJobSettings.PackageIdVar)]
    [InlineData(WorkerJobSettings.TargetVersionVar)]
    [InlineData(WorkerJobSettings.WorkspaceRootVar)]
    [InlineData(WorkerJobSettings.GitTokenVar)]
    public void Worker_fails_when_a_required_environment_variable_is_missing(string missingVar)
    {
        var env = new Dictionary<string, string?>(Complete) { [missingVar] = null };

        var ex = Assert.Throws<MissingWorkerConfigurationException>(
            () => WorkerJobSettings.FromEnvironment(name => env.GetValueOrDefault(name)));

        Assert.Contains(missingVar, ex.MissingVariables);
    }

    [Fact]
    public void Worker_reports_all_missing_variables_at_once()
    {
        var ex = Assert.Throws<MissingWorkerConfigurationException>(
            () => WorkerJobSettings.FromEnvironment(_ => null));

        Assert.Contains(WorkerJobSettings.JobIdVar, ex.MissingVariables);
        Assert.Contains(WorkerJobSettings.GitTokenVar, ex.MissingVariables);
    }
}
