namespace DevAgent.Forge.Tests;

using DevAgent.Forge.Tools;
using Xunit;

public class UnifiedDiffTests
{
    [Fact]
    public void Applies_a_simple_replacement_hunk()
    {
        var original = "line1\nline2\nline3\n";
        var diff = "@@ -1,3 +1,3 @@\n line1\n-line2\n+CHANGED\n line3\n";

        var result = UnifiedDiff.Apply(original, diff);

        Assert.True(result.Success);
        Assert.Equal("line1\nCHANGED\nline3", result.NewContent);
    }

    [Fact]
    public void Applies_pure_addition()
    {
        var original = "a\nb\n";
        var diff = "@@ -1,2 +1,3 @@\n a\n b\n+c\n";

        var result = UnifiedDiff.Apply(original, diff);

        Assert.True(result.Success);
        Assert.Equal("a\nb\nc", result.NewContent);
    }

    [Fact]
    public void Rejects_patch_with_context_mismatch()
    {
        var original = "alpha\nbeta\n";
        var diff = "@@ -1,2 +1,2 @@\n WRONG\n-beta\n+gamma\n";

        var result = UnifiedDiff.Apply(original, diff);

        Assert.False(result.Success);
        Assert.Contains("mismatch", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_removal_that_does_not_match_source()
    {
        var original = "a\nb\n";
        var diff = "@@ -1,2 +1,1 @@\n a\n-NOTB\n";

        var result = UnifiedDiff.Apply(original, diff);

        Assert.False(result.Success);
    }

    [Fact]
    public void Create_then_apply_roundtrips()
    {
        var before = "one\ntwo\nthree\n";
        var after = "one\nTWO\nthree\n";

        var diff = UnifiedDiff.Create("f.cs", before, after);
        var result = UnifiedDiff.Apply(before, diff);

        Assert.True(result.Success);
        Assert.Equal("one\nTWO\nthree", result.NewContent);
    }
}
