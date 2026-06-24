namespace DevAgent.Guard.Tests;

using DevAgent.Contracts.Jobs;
using DevAgent.Guard.Policies;
using Xunit;

public class AllowlistPolicyTests
{
    [Fact]
    public void Repository_must_be_allowlisted()
    {
        var policy = new RepositoryPolicy(new[]
        {
            new RepositoryEntry { Key = "svc-a", CloneUrl = "https://git/svc-a.git" },
        });

        Assert.True(policy.Validate("svc-a").IsValid);
        Assert.False(policy.Validate("svc-b").IsValid);
        Assert.False(policy.Validate("").IsValid);
    }

    [Fact]
    public void Repository_resolution_rejects_unknown_key_instead_of_returning_url()
    {
        var policy = new RepositoryPolicy(Array.Empty<RepositoryEntry>());
        Assert.Throws<DevAgent.Contracts.Validation.PolicyViolationException>(() => policy.Resolve("nope"));
    }

    [Fact]
    public void Package_id_must_be_allowlisted()
    {
        var policy = new PackagePolicy(new[] { "Serilog" });

        Assert.True(policy.Validate("Serilog").IsValid);
        Assert.True(policy.Validate("serilog").IsValid); // case-insensitive
        Assert.False(policy.Validate("EvilPackage").IsValid);
    }

    [Fact]
    public void Container_image_must_be_allowlisted()
    {
        var policy = new ContainerImagePolicy(new[] { "registry/worker:8.0" });

        Assert.True(policy.Validate("registry/worker:8.0").IsValid);
        Assert.False(policy.Validate("attacker/image:latest").IsValid);
    }

    [Fact]
    public void Job_type_must_be_allowlisted()
    {
        var policy = new JobPolicy(new Dictionary<AgentJobType, string>
        {
            [AgentJobType.NuGetUpdate] = "registry/worker:8.0",
        });

        Assert.True(policy.Validate(AgentJobType.NuGetUpdate).IsValid);
        Assert.False(policy.Validate(AgentJobType.LlmAssistedFix).IsValid);
        Assert.False(policy.Validate(AgentJobType.Unknown).IsValid);
    }

    [Fact]
    public void Job_type_image_resolution_rejects_disallowed_type()
    {
        var policy = new JobPolicy(new Dictionary<AgentJobType, string>());
        Assert.Throws<DevAgent.Contracts.Validation.PolicyViolationException>(
            () => policy.ResolveImage(AgentJobType.NuGetUpdate));
    }
}
