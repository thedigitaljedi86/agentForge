namespace DevAgent.Contracts.Validation;

/// <summary>
/// Shared validation contract used by Guard policies and the Runner.
/// A small, allocation-light result type so security checks can return
/// a precise reason without throwing for expected rejections.
/// </summary>
public sealed record ValidationResult
{
    private ValidationResult(bool isValid, string? reason)
    {
        IsValid = isValid;
        Reason = reason;
    }

    public bool IsValid { get; }

    /// <summary>Reason for rejection. Null when <see cref="IsValid"/> is true.</summary>
    public string? Reason { get; }

    public static ValidationResult Success { get; } = new(true, null);

    public static ValidationResult Fail(string reason) => new(false, reason);

    /// <summary>Throws if invalid. Use only where a rejection is exceptional.</summary>
    public void EnsureValid()
    {
        if (!IsValid)
        {
            throw new PolicyViolationException(Reason ?? "Policy validation failed.");
        }
    }
}

/// <summary>
/// Thrown when a security policy is violated. Distinct type so callers can
/// catch policy failures separately from generic exceptions and audit them.
/// </summary>
public sealed class PolicyViolationException : Exception
{
    public PolicyViolationException(string message) : base(message) { }
}
