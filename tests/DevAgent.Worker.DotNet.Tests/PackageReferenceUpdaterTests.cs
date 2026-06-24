namespace DevAgent.Worker.DotNet.Tests;

using DevAgent.Worker.DotNet;
using Xunit;

public class PackageReferenceUpdaterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "devagent-upd-" + Guid.NewGuid().ToString("N"));

    public PackageReferenceUpdaterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string WriteProject(string content)
    {
        var path = Path.Combine(_dir, "App.csproj");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Updates_package_reference_version()
    {
        var path = WriteProject(
            "<Project><ItemGroup><PackageReference Include=\"Serilog\" Version=\"2.0.0\" /></ItemGroup></Project>");

        var outcome = new PackageReferenceUpdater().UpdateInDirectory(_dir, "Serilog", "3.1.1");

        Assert.True(outcome.Changed);
        Assert.Contains("Version=\"3.1.1\"", File.ReadAllText(path));
    }

    [Fact]
    public void Does_not_downgrade_when_only_upgrade_is_set()
    {
        var path = WriteProject(
            "<Project><ItemGroup><PackageReference Include=\"Serilog\" Version=\"4.0.0\" /></ItemGroup></Project>");

        var outcome = new PackageReferenceUpdater(onlyUpgrade: true).UpdateInDirectory(_dir, "Serilog", "3.1.1");

        Assert.False(outcome.Changed);
        Assert.Contains("Version=\"4.0.0\"", File.ReadAllText(path));
    }

    [Fact]
    public void Reports_no_change_when_package_absent()
    {
        WriteProject("<Project><ItemGroup><PackageReference Include=\"Polly\" Version=\"8.0.0\" /></ItemGroup></Project>");

        var outcome = new PackageReferenceUpdater().UpdateInDirectory(_dir, "Serilog", "3.1.1");

        Assert.False(outcome.Changed);
        Assert.Equal(0, outcome.FilesUpdated);
    }

    [Theory]
    [InlineData("1.2.9", "1.2.10", -1)]
    [InlineData("2.0.0", "2.0.0", 0)]
    [InlineData("4.0.0", "3.1.1", 1)]
    public void Version_comparison_is_numeric(string a, string b, int expectedSign)
    {
        Assert.Equal(expectedSign, Math.Sign(PackageReferenceUpdater.CompareVersions(a, b)));
    }
}
