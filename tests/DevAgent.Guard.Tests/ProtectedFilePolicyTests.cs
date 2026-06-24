namespace DevAgent.Guard.Tests;

using DevAgent.Guard.Policies;
using Xunit;

public class ProtectedFilePolicyTests
{
    private readonly ProtectedFilePolicy _policy = new();

    [Theory]
    [InlineData(".env")]
    [InlineData("src/.env")]
    [InlineData("secrets.json")]
    [InlineData("appsettings.Production.json")]
    [InlineData("deploy/Dockerfile")]
    [InlineData("infra/main.tf")]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData("certs/server.pfx")]
    [InlineData("k8s/deployment.yaml")]
    public void Protected_files_cannot_be_edited(string path)
    {
        Assert.True(_policy.IsProtected(path));
        Assert.False(_policy.ValidateEditable(path).IsValid);
    }

    [Theory]
    [InlineData("src/Program.cs")]
    [InlineData("README.md")]
    [InlineData("src/MyApp/MyApp.csproj")]
    public void Ordinary_source_files_are_editable(string path)
    {
        Assert.False(_policy.IsProtected(path));
        Assert.True(_policy.ValidateEditable(path).IsValid);
    }
}
