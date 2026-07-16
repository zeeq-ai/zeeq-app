namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Centralized constants for the GitHub Check Runs integration.
/// </summary>
/// <remarks>
/// The check name is a compatibility contract. GitHub Rulesets reference this
/// context by name, so changing it silently breaks every repository whose
/// branch-protection ruleset was imported with the old value. Keep it stable.
/// </remarks>
public static class CheckRunConstants
{
    /// <summary>
    /// Published GitHub check-run context name used by branch-protection rulesets.
    /// </summary>
    public const string ZeeqCheckRunName = "Zeeq Code Review";
}
