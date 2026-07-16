namespace Zeeq.Core.Models;

/// <summary>
/// Repository check-run gating configuration stored as typed JSONB on the review config.
/// </summary>
/// <remarks>
/// When enabled, completed reviews that meet the severity threshold publish a
/// blocking <c>action_required</c> check run on the PR head commit. The block is
/// surfaced to users in the GitHub PR UI and in the Zeeq comment rendering.
/// Enforcement requires the operator to import a branch-protection ruleset that
/// references the check context <c>Zeeq Code Review</c>.
/// </remarks>
public sealed class CodeRepositoryReviewCheckRunConfiguration
{
    /// <summary>
    /// Minimal empty configuration (feature disabled).
    /// </summary>
    public static CodeRepositoryReviewCheckRunConfiguration Empty { get; } = new();

    /// <summary>
    /// Block when the review has at least one Critical finding.
    /// </summary>
    public bool BlockOnCritical { get; set; }

    /// <summary>
    /// Block when the review has at least one Major finding.
    /// Implies Critical, so selecting Major also blocks on Critical findings.
    /// </summary>
    public bool BlockOnMajor { get; set; }

    /// <summary>
    /// Feature is active only when at least one severity threshold is selected.
    /// </summary>
    public bool IsEnabled => BlockOnCritical || BlockOnMajor;

    /// <summary>
    /// True when the finding counts meet the configured blocking threshold.
    /// </summary>
    /// <remarks>
    /// Major implies Critical, so a Major selection blocks on either severity.
    /// </remarks>
    /// <param name="criticalFindings">Number of Critical findings in the review.</param>
    /// <param name="majorFindings">Number of Major findings in the review.</param>
    public bool ShouldBlock(int criticalFindings, int majorFindings) =>
        (BlockOnMajor && (majorFindings > 0 || criticalFindings > 0))
        || (BlockOnCritical && criticalFindings > 0);
}
