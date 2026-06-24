namespace DevAgent.Runner.Tests;

using DevAgent.Contracts.Jobs;
using Xunit;

/// <summary>
/// These tests lock in the design rule that callers cannot supply
/// infrastructure: no raw repository URL, no container image and no Docker
/// arguments are accepted on the request surface. Repositories and images are
/// resolved from allowlists by KEY only.
/// </summary>
public class NoUserSuppliedInfrastructureTests
{
    [Theory]
    [InlineData("CloneUrl")]
    [InlineData("RepositoryUrl")]
    [InlineData("Url")]
    public void User_supplied_repository_urls_are_rejected(string forbidden)
    {
        Assert.Null(typeof(NuGetUpdateJobRequest).GetProperty(forbidden));
        Assert.Null(typeof(StartNuGetUpdateRunnerRequest).GetProperty(forbidden));
    }

    [Theory]
    [InlineData("ContainerImage")]
    [InlineData("Image")]
    public void User_supplied_container_images_are_rejected(string forbidden)
    {
        Assert.Null(typeof(NuGetUpdateJobRequest).GetProperty(forbidden));
        Assert.Null(typeof(StartNuGetUpdateRunnerRequest).GetProperty(forbidden));
    }

    [Theory]
    [InlineData("DockerArgs")]
    [InlineData("DockerArguments")]
    [InlineData("ContainerArgs")]
    [InlineData("Command")]
    [InlineData("Arguments")]
    public void User_supplied_docker_arguments_are_rejected(string forbidden)
    {
        Assert.Null(typeof(NuGetUpdateJobRequest).GetProperty(forbidden));
        Assert.Null(typeof(StartNuGetUpdateRunnerRequest).GetProperty(forbidden));
    }

    [Fact]
    public void Request_exposes_only_allowlist_keys_and_version()
    {
        // The caller's vocabulary is intentionally tiny: keys + version.
        Assert.NotNull(typeof(StartNuGetUpdateRunnerRequest).GetProperty("RepositoryKey"));
        Assert.NotNull(typeof(StartNuGetUpdateRunnerRequest).GetProperty("PackageId"));
        Assert.NotNull(typeof(StartNuGetUpdateRunnerRequest).GetProperty("TargetVersion"));
    }

    [Theory]
    [InlineData("CloneUrl")]
    [InlineData("ContainerImage")]
    [InlineData("Image")]
    [InlineData("DockerArgs")]
    [InlineData("Command")]
    public void DotNetUpgrade_request_also_forbids_infrastructure(string forbidden)
    {
        Assert.Null(typeof(DotNetUpgradeJobRequest).GetProperty(forbidden));
        Assert.Null(typeof(StartDotNetUpgradeRunnerRequest).GetProperty(forbidden));
    }

    [Fact]
    public void DotNetUpgrade_request_exposes_only_a_key_and_framework()
    {
        Assert.NotNull(typeof(StartDotNetUpgradeRunnerRequest).GetProperty("RepositoryKey"));
        Assert.NotNull(typeof(StartDotNetUpgradeRunnerRequest).GetProperty("TargetFramework"));
    }
}
