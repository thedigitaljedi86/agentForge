namespace DevAgent.Forge;

using DevAgent.Audit;

/// <summary>
/// The controlled LLM coding agent loop. On each iteration it asks the LLM for
/// the next decision, runs the requested tool through the policy-enforcing
/// handler, records the result, and repeats until the model declares completion
/// or the iteration cap is hit.
///
/// SECURITY INVARIANTS enforced here:
///   * The model can only ever ask for a structured tool call — never a shell.
///   * Every tool call is validated + audited by the handler before running.
///   * The loop is bounded by <see cref="CodingAgentOptions.MaxIterations"/>.
///   * The final diff and the model's reasoning summary are saved + audited.
///   * The agent never merges anything; it only edits files in the workspace.
///     Pushing the branch and opening the (review-required) PR remain the
///     worker's deterministic responsibility.
/// </summary>
public sealed class CodingAgent : ICodingAgent
{
    private readonly ILlmClient _llm;
    private readonly IToolCallHandler _handler;
    private readonly IAuditLog _audit;
    private readonly CodingAgentOptions _options;

    public CodingAgent(
        ILlmClient llm,
        IToolCallHandler handler,
        IAuditLog audit,
        CodingAgentOptions? options = null)
    {
        _llm = llm;
        _handler = handler;
        _audit = audit;
        _options = options ?? new CodingAgentOptions();
    }

    public async Task<CodingAgentResult> RunAsync(CodingAgentTask task, CancellationToken cancellationToken = default)
    {
        var steps = new List<AgentStep>();
        var changedFiles = new List<string>();
        var diffBuilder = new System.Text.StringBuilder();
        string? summary = null;
        var stoppedAtLimit = false;
        var iterations = 0;

        while (true)
        {
            if (iterations >= _options.MaxIterations)
            {
                // SECURITY: hard stop. Don't let a misbehaving model loop forever.
                stoppedAtLimit = true;
                break;
            }

            iterations++;

            var decision = await _llm.GetNextDecisionAsync(task, steps, cancellationToken).ConfigureAwait(false);

            // Record the model's reasoning for this step as a prompt audit entry.
            if (!string.IsNullOrWhiteSpace(decision.Reasoning))
            {
                await _audit.WriteAsync(new PromptAuditEvent
                {
                    JobId = task.JobId,
                    Actor = "CodingAgent",
                    Prompt = decision.Reasoning!,
                }, cancellationToken);
            }

            if (decision.IsComplete || decision.ToolCall is null)
            {
                summary = decision.Summary ?? decision.Reasoning;
                break;
            }

            var result = await _handler.HandleAsync(decision.ToolCall, cancellationToken).ConfigureAwait(false);
            steps.Add(new AgentStep
            {
                Request = decision.ToolCall,
                Result = result,
                Reasoning = decision.Reasoning,
            });

            if (result is { ChangedFile: { } file })
            {
                if (!changedFiles.Contains(file))
                {
                    changedFiles.Add(file);
                }

                if (result.Diff is { Length: > 0 } diff)
                {
                    diffBuilder.Append(diff);
                    if (!diff.EndsWith('\n'))
                    {
                        diffBuilder.Append('\n');
                    }
                }
            }
        }

        var finalDiff = diffBuilder.ToString();

        // Save the final aggregated diff so the change is fully reviewable.
        if (finalDiff.Length > 0)
        {
            await _audit.WriteAsync(new DiffAuditEvent
            {
                JobId = task.JobId,
                Actor = "CodingAgent",
                FilePath = "(aggregated)",
                UnifiedDiff = finalDiff,
            }, cancellationToken);
        }

        // The run "succeeded" if the model completed on its own (not cut off)
        // and no tool call was denied by policy along the way.
        var anyDenied = steps.Any(s => s.Result.DeniedByPolicy);
        var succeeded = !stoppedAtLimit && summary is not null && !anyDenied;

        return new CodingAgentResult
        {
            JobId = task.JobId,
            Succeeded = succeeded,
            ReasoningSummary = summary,
            FinalDiff = finalDiff,
            ChangedFiles = changedFiles,
            IterationsUsed = iterations,
            StoppedAtIterationLimit = stoppedAtLimit,
            Steps = steps,
        };
    }
}
