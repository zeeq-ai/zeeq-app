using Microsoft.EntityFrameworkCore;
using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;

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
        var organization = await db
            .Organizations.AsNoTracking()
            .TagWithOperationCallSite("code_review_organization_settings.get")
            .FirstOrDefaultAsync(row => row.Id == organizationId, cancellationToken)
            ?? throw new InvalidOperationException($"Organization {organizationId} was not found.");

        return organization.CodeReviewConfiguration ?? CodeReviewOrganizationSettings.Default;
    }

    /// <inheritdoc />
    public async Task<CodeReviewOrganizationSettings> SaveAsync(
        string organizationId,
        int? maxConcurrentReviews,
        bool? allowAgentAttestedTriage,
        CancellationToken cancellationToken
    )
    {
        var defaults = System.Text.Json.JsonSerializer.Serialize(
            CodeReviewOrganizationSettings.Default
        );
        var rows = await db.Database.ExecuteSqlInterpolatedAsync(
            $$"""
            UPDATE zeeq.core_organizations
            SET code_review_configuration =
                jsonb_set(
                    jsonb_set(COALESCE(code_review_configuration, {{defaults}}::jsonb),
                        '{MaxConcurrentReviews}',
                        CASE WHEN {{maxConcurrentReviews}}::integer IS NULL THEN COALESCE(code_review_configuration->'MaxConcurrentReviews', '4'::jsonb) ELSE to_jsonb({{maxConcurrentReviews}}::integer) END),
                    '{AllowAgentAttestedTriage}',
                    CASE WHEN {{allowAgentAttestedTriage}}::boolean IS NULL THEN COALESCE(code_review_configuration->'AllowAgentAttestedTriage', 'null'::jsonb) ELSE to_jsonb({{allowAgentAttestedTriage}}::boolean) END),
                updated_at_utc = now()
            WHERE id = {{organizationId}}
            """,
            cancellationToken
        );
        if (rows != 1)
            throw new InvalidOperationException($"Organization {organizationId} was not found.");
        return await GetAsync(organizationId, cancellationToken);
    }
}
