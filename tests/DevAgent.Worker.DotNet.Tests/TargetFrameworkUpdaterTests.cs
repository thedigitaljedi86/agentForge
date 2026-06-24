namespace DevAgent.Worker.DotNet.Tests;

using DevAgent.Worker.DotNet;
using Xunit;

public class TargetFrameworkUpdaterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devagent-tfm-" + Guid.NewGuid().ToString("N"));

    public TargetFrameworkUpdaterTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string WriteProject(string name, string body)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, body);
        return path;
    }

    private static string SingleTarget(string tfm) =>
        $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{tfm}</TargetFramework>\n  </PropertyGroup>\n</Project>\n";

    [Fact]
    public void Upgrades_single_target_framework_across_all_projects()
    {
        WriteProject("a/A.csproj", SingleTarget("net6.0"));
        WriteProject("b/B.csproj", SingleTarget("net7.0"));

        var outcome = new TargetFrameworkUpdater().UpdateInDirectory(_root, "net8.0");

        Assert.True(outcome.Changed);
        Assert.Equal(2, outcome.ProjectsUpdated);
        Assert.Contains("net8.0", File.ReadAllText(Path.Combine(_root, "a/A.csproj")));
        Assert.Contains("net8.0", File.ReadAllText(Path.Combine(_root, "b/B.csproj")));
    }

    [Fact]
    public void Never_downgrades_a_newer_project()
    {
        WriteProject("a/A.csproj", SingleTarget("net9.0"));

        var outcome = new TargetFrameworkUpdater().UpdateInDirectory(_root, "net8.0");

        Assert.False(outcome.Changed);
        Assert.Contains("net9.0", File.ReadAllText(Path.Combine(_root, "a/A.csproj")));
    }

    [Fact]
    public void Leaves_netstandard_and_legacy_frameworks_untouched()
    {
        WriteProject("lib/Lib.csproj", SingleTarget("netstandard2.0"));
        WriteProject("old/Old.csproj", SingleTarget("net48"));

        var outcome = new TargetFrameworkUpdater().UpdateInDirectory(_root, "net8.0");

        Assert.False(outcome.Changed);
        Assert.Contains("netstandard2.0", File.ReadAllText(Path.Combine(_root, "lib/Lib.csproj")));
        Assert.Contains("net48", File.ReadAllText(Path.Combine(_root, "old/Old.csproj")));
    }

    [Fact]
    public void Upgrades_only_modern_entries_in_multi_targeting_and_dedupes()
    {
        WriteProject("m/M.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFrameworks>net6.0;netstandard2.0;net8.0</TargetFrameworks>\n  </PropertyGroup>\n</Project>\n");

        var outcome = new TargetFrameworkUpdater().UpdateInDirectory(_root, "net8.0");

        Assert.True(outcome.Changed);
        var text = File.ReadAllText(Path.Combine(_root, "m/M.csproj"));
        // net6.0 -> net8.0, then deduped with the existing net8.0; netstandard kept.
        Assert.Contains("<TargetFrameworks>net8.0;netstandard2.0</TargetFrameworks>", text);
    }

    [Fact]
    public void Rejects_a_non_modern_target_framework()
    {
        WriteProject("a/A.csproj", SingleTarget("net6.0"));

        var outcome = new TargetFrameworkUpdater().UpdateInDirectory(_root, "net48");

        Assert.False(outcome.Changed);
        Assert.Contains("not a modern", outcome.Message);
    }

    [Fact]
    public void Reports_no_change_when_everything_already_meets_target()
    {
        WriteProject("a/A.csproj", SingleTarget("net8.0"));

        var outcome = new TargetFrameworkUpdater().UpdateInDirectory(_root, "net8.0");

        Assert.False(outcome.Changed);
        Assert.Equal(0, outcome.ProjectsUpdated);
    }

    [Theory]
    [InlineData("net6.0", "net8.0", -1)]
    [InlineData("net8.0", "net8.0", 0)]
    [InlineData("net10.0", "net8.0", 1)]
    public void CompareTfm_orders_numerically(string a, string b, int expectedSign)
    {
        Assert.Equal(expectedSign, Math.Sign(TargetFrameworkUpdater.CompareTfm(a, b)));
    }
}
