namespace DevAgent.Runner.Tests;

using DevAgent.Guard.Policies;
using DevAgent.Runner.Api.Application;

/// <summary>Test adapter: serves a fixed policy set.</summary>
internal sealed class StaticGuardPolicySource : IGuardPolicySource
{
    private readonly GuardPolicySet _set;

    public StaticGuardPolicySource(GuardPolicySet set) => _set = set;

    public ValueTask<GuardPolicySet> GetAsync(CancellationToken cancellationToken = default) => new(_set);
}
