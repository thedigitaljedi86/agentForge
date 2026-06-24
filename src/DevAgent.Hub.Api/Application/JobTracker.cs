namespace DevAgent.Hub.Api.Application;

using System.Collections.Concurrent;
using DevAgent.Contracts.Jobs;

/// <summary>
/// A single tracked job, as shown on the status dashboard: which agent received
/// the task, what it targets, and where it currently stands.
/// </summary>
public sealed record JobRecord
{
    public required string JobId { get; init; }

    /// <summary>The agent (or "manual") that received the task.</summary>
    public required string Agent { get; init; }

    /// <summary>Job type, e.g. NuGetUpdate or DotNetUpgrade.</summary>
    public required string JobType { get; init; }

    /// <summary>Human-readable target, e.g. "starwars-quotes → net10.0".</summary>
    public required string Target { get; init; }

    public required AgentJobStatus Status { get; init; }

    public string? Message { get; init; }

    public required DateTimeOffset ReceivedAtUtc { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; init; }
}

/// <summary>
/// Tracks the jobs agents have received and their latest status, for the
/// dashboard. In-memory (per process) by design — the first milestone keeps the
/// platform dependency-free; a durable store would back this in production.
/// </summary>
public interface IJobTracker
{
    /// <summary>Insert or update a job by id, preserving the original received time.</summary>
    void Upsert(
        string jobId,
        string agent,
        string jobType,
        string target,
        AgentJobStatus status,
        string? message);

    /// <summary>All tracked jobs, newest activity first.</summary>
    IReadOnlyList<JobRecord> Snapshot();
}

/// <summary>Thread-safe in-memory <see cref="IJobTracker"/>.</summary>
public sealed class InMemoryJobTracker : IJobTracker
{
    private readonly ConcurrentDictionary<string, JobRecord> _jobs = new();

    public void Upsert(string jobId, string agent, string jobType, string target, AgentJobStatus status, string? message)
    {
        var now = DateTimeOffset.UtcNow;
        _jobs.AddOrUpdate(
            jobId,
            _ => new JobRecord
            {
                JobId = jobId,
                Agent = agent,
                JobType = jobType,
                Target = target,
                Status = status,
                Message = message,
                ReceivedAtUtc = now,
                UpdatedAtUtc = now,
            },
            (_, existing) => existing with
            {
                Status = status,
                Message = message ?? existing.Message,
                UpdatedAtUtc = now,
            });
    }

    public IReadOnlyList<JobRecord> Snapshot() =>
        _jobs.Values.OrderByDescending(j => j.UpdatedAtUtc).ToList();
}
