namespace Zeeq.Core.Models;

/// <summary>
/// Typed JSONB configuration document for repository-level review settings.
/// </summary>
/// <remarks>
/// This replaces raw JSON access at call sites while preserving the existing
/// repository configuration column. Repository filters are evaluated when
/// building one review context and are not independently queried in this slice.
/// </remarks>
public sealed class CodeRepositoryReviewConfiguration
{
    /// <summary>Default configuration for repositories with no review settings.</summary>
    public static CodeRepositoryReviewConfiguration Empty { get; } = new();

    /// <summary>Repository-level file filter applied before agent activation.</summary>
    public CodeReviewFileFilter FileFilter { get; set; } = new();

    /// <summary>
    /// Check-run gating settings for this repository.
    /// Defaults to disabled when absent from the stored JSONB document.
    /// </summary>
    public CodeRepositoryReviewCheckRunConfiguration CheckRun { get; set; } =
        CodeRepositoryReviewCheckRunConfiguration.Empty;

    /// <summary>
    /// Shared prompt fragment injected into every reviewer agent's prompt for
    /// this repository. Empty means no organization-wide guidance is added.
    /// </summary>
    public string SharedPromptFragment { get; set; } = string.Empty;
}
