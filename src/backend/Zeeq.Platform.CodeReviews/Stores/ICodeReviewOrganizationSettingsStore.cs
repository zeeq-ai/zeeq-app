using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Store for organization-level code-review execution settings.
/// </summary>
public interface ICodeReviewOrganizationSettingsStore
{
    /// <summary>
    /// Gets effective organization settings, using defaults when no JSON config exists.
    /// </summary>
    Task<CodeReviewOrganizationSettings> GetAsync(
        string organizationId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Saves organization-level execution settings.
    /// </summary>
    Task<CodeReviewOrganizationSettings> SaveAsync(
        string organizationId,
        CodeReviewOrganizationSettings settings,
        CancellationToken cancellationToken
    );
}
