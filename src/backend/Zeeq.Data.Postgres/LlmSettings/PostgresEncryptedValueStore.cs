using Zeeq.Core.Llm;
using Zeeq.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Zeeq.Data.Postgres.LlmSettings;

/// <summary>
/// Postgres-backed store for organization-owned encrypted values.
/// </summary>
internal sealed class PostgresEncryptedValueStore(PostgresDbContext db) : IEncryptedValueStore
{
    /// <summary>
    /// Lists active encrypted values for one organization and kind.
    /// </summary>
    public async Task<IReadOnlyList<EncryptedValue>> ListActiveAsync(
        string organizationId,
        EncryptedValueKind kind,
        CancellationToken cancellationToken
    ) =>
        await db
            .EncryptedValues.TagWithOperationCallSite("encrypted_value.list_active")
            .Where(value =>
                value.OrganizationId == organizationId
                && value.Kind == kind
                && value.DisabledAtUtc == null
            )
            .OrderBy(value => value.Name)
            .ThenBy(value => value.CreatedAtUtc)
            .ThenBy(value => value.Id)
            .ToArrayAsync(cancellationToken);

    /// <summary>
    /// Finds one active encrypted value by organization and id.
    /// </summary>
    public Task<EncryptedValue?> FindActiveAsync(
        string organizationId,
        string id,
        CancellationToken cancellationToken
    ) =>
        db
            .EncryptedValues.TagWithOperationCallSite("encrypted_value.find_active")
            .FirstOrDefaultAsync(
                value =>
                    value.OrganizationId == organizationId
                    && value.Id == id
                    && value.DisabledAtUtc == null,
                cancellationToken
            );

    /// <summary>
    /// Adds a new encrypted value.
    /// </summary>
    public async Task<EncryptedValue> AddAsync(
        EncryptedValue value,
        CancellationToken cancellationToken
    )
    {
        db.EncryptedValues.Add(value);
        await db.SaveChangesAsync(cancellationToken);
        return value;
    }

    /// <summary>
    /// Updates mutable fields on one active encrypted value.
    /// </summary>
    public async Task<bool> UpdateAsync(EncryptedValue value, CancellationToken cancellationToken)
    {
        var existing = await FindActiveAsync(value.OrganizationId, value.Id, cancellationToken);

        if (existing is null)
        {
            return false;
        }

        existing.EncryptionProvider = value.EncryptionProvider;
        existing.Name = value.Name;
        existing.Ciphertext = value.Ciphertext;
        existing.UpdatedAtUtc = value.UpdatedAtUtc;

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Soft-disables one active encrypted value.
    /// </summary>
    public async Task<bool> DisableAsync(
        string organizationId,
        string id,
        DateTimeOffset disabledAtUtc,
        CancellationToken cancellationToken
    )
    {
        var existing = await FindActiveAsync(organizationId, id, cancellationToken);

        if (existing is null)
        {
            return false;
        }

        existing.DisabledAtUtc = disabledAtUtc;
        existing.UpdatedAtUtc = disabledAtUtc;

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
