namespace DevAgent.Guard.Tests;

using DevAgent.Guard.Policies;
using Xunit;

public class TargetFrameworkPolicyTests
{
    [Theory]
    [InlineData("net8.0")]
    [InlineData("net10.0")]
    [InlineData("NET8.0")]
    public void Format_only_policy_accepts_modern_monikers(string tfm)
    {
        Assert.True(new TargetFrameworkPolicy().IsAllowed(tfm));
    }

    [Theory]
    [InlineData("net48")]
    [InlineData("netstandard2.0")]
    [InlineData("net8")]
    [InlineData("")]
    [InlineData("net8.0; rm -rf /")]
    public void Format_only_policy_rejects_non_modern_or_malformed(string tfm)
    {
        Assert.False(new TargetFrameworkPolicy().IsAllowed(tfm));
    }

    [Fact]
    public void Allowlist_restricts_to_configured_frameworks()
    {
        var policy = new TargetFrameworkPolicy(new[] { "net8.0" });
        Assert.True(policy.IsAllowed("net8.0"));
        Assert.False(policy.IsAllowed("net7.0")); // well-formed but not allowlisted
    }
}
