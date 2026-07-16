using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;
using Microsoft.EntityFrameworkCore;

namespace Zeeq.Data.Postgres.CodeReviews;

/// <summary>
/// Postgres-backed store for organization code-review settings.
/// </summary>
internal sealed class PostgresCodeReviewOrganizationSettingsStore(PostgresDbContext db)
    : ICodeReviewOrganizationSettingsStore
{
    /// <inheritdoc />
    public async Task<CodeReviewOrganizationSettings> GetAsync(
        string organizationId,
        CancellationToken cancellationToken
    )
    {
        var organization =
            await db
                .Organizations.TagWithOperationCallSite("code_review_organization_settings.get")
                .FirstOrDefaultAsync(row => row.Id == organizationId, cancellationToken)
            ?? throw new InvalidOperationException($"Organization {organizationId} was not found.");

        return organization.CodeReviewConfiguration ?? CodeReviewOrganizationSettings.Default;
    }

    /// <inheritdoc />
    public async Task<CodeReviewOrganizationSettings> SaveAsync(
        string organizationId,
        CodeReviewOrganizationSettings settings,
        CancellationToken cancellationToken
    )
    {
        var existing =
            await db
                .Organizations.TagWithOperationCallSite("code_review_organization_settings.save")
                .FirstOrDefaultAsync(row => row.Id == organizationId, cancellationToken)
            ?? throw new InvalidOperationException($"Organization {organizationId} was not found.");

        existing.CodeReviewConfiguration = settings;
        existing.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(cancellationToken);

        return settings;
    }
}
