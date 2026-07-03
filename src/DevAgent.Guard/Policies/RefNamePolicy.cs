namespace DevAgent.Guard.Policies;

using System.Text.RegularExpressions;
using DevAgent.Contracts.Validation;

/// <summary>
/// Validates caller-supplied git ref (branch) names before they reach any
/// command. Branch names arrive from CI webhooks and PR events — external
/// input — so they are constrained to a conservative character set long before
/// the argument-vector defence: letters, digits, dot, underscore, slash, dash.
///
/// SECURITY: This intentionally rejects valid-but-exotic git refs. A branch
/// that cannot pass this filter cannot be processed by the platform.
/// </summary>
public static partial class RefNamePolicy
{
    private const int MaxLength = 200;

    [GeneratedRegex("^[A-Za-z0-9._/-]+$")]
    private static partial Regex Allowed();

    public static ValidationResult Validate(string? refName)
    {
        if (string.IsNullOrWhiteSpace(refName))
        {
            return ValidationResult.Fail("Branch name must not be empty.");
        }

        if (refName.Length > MaxLength)
        {
            return ValidationResult.Fail($"Branch name exceeds {MaxLength} characters.");
        }

        if (!Allowed().IsMatch(refName))
        {
            return ValidationResult.Fail(
                $"Branch name '{refName}' contains disallowed characters (allowed: letters, digits, '.', '_', '/', '-').");
        }

        // A leading dash could be parsed as an option by git even in an
        // argument vector (e.g. "-b"); refuse it outright.
        if (refName.StartsWith('-'))
        {
            return ValidationResult.Fail("Branch name must not start with '-'.");
        }

        return ValidationResult.Success;
    }
}
