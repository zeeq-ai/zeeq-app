using Zeeq.Core.Llm;
using Zeeq.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Zeeq.Data.Postgres.LlmSettings;

/// <summary>
/// Postgres-backed store for organization LLM tier configuration.
/// </summary>
internal sealed class PostgresLlmSettingsStore(PostgresDbContext db) : ILlmSettingsStore
{
    /// <summary>
    /// Finds the typed LLM configuration for one organization without returning the organization entity.
    /// </summary>
    /// <remarks>
    /// Organizations still carrying the pre-Fireworks migration-seeded
    /// DeepSeek-direct default are treated as having no saved configuration so
    /// callers fall back to the current Fireworks system default instead of
    /// failing the Fireworks-only internal-key policy.
    /// </remarks>
    public async Task<OrganizationLlmConfiguration?> FindConfigurationAsync(
        string organizationId,
        CancellationToken cancellationToken
    )
    {
        var configuration = await db
            .Organizations.TagWithOperationCallSite("llm_settings.find_configuration")
            .Where(organization => organization.Id == organizationId)
            .Select(organization => organization.LlmConfiguration)
            .FirstOrDefaultAsync(cancellationToken);

        return configuration is null || configuration.IsLegacyDeepSeekDefault()
            ? null
            : configuration;
    }

    /// <summary>
    /// Replaces the typed LLM configuration for one organization.
    /// </summary>
    public async Task<bool> UpdateConfigurationAsync(
        string organizationId,
        OrganizationLlmConfiguration configuration,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken
    )
    {
        var organization = await db
            .Organizations.TagWithOperationCallSite("llm_settings.update_find")
            .FirstOrDefaultAsync(
                organization => organization.Id == organizationId,
                cancellationToken
            );

        if (organization is null)
        {
            return false;
        }

        organization.LlmConfiguration = configuration;
        organization.UpdatedAtUtc = updatedAtUtc;

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
