using Zeeq.Core.Models;

namespace Zeeq.Core.Llm;

/// <summary>
/// Store for organization-owned encrypted secret values.
/// </summary>
/// <remarks>
/// Store methods are explicitly organization-scoped because encrypted values
/// use <c>OrganizationId</c> as the leading primary-key column.
/// </remarks>
public interface IEncryptedValueStore
{
    /// <summary>
    /// Lists active encrypted values for one organization and kind.
    /// </summary>
    Task<IReadOnlyList<EncryptedValue>> ListActiveAsync(
        string organizationId,
        EncryptedValueKind kind,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Finds one active encrypted value by organization and id.
    /// </summary>
    Task<EncryptedValue?> FindActiveAsync(
        string organizationId,
        string id,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Adds a new encrypted value.
    /// </summary>
    Task<EncryptedValue> AddAsync(EncryptedValue value, CancellationToken cancellationToken);

    /// <summary>
    /// Updates mutable encrypted-value fields for one active value.
    /// </summary>
    Task<bool> UpdateAsync(EncryptedValue value, CancellationToken cancellationToken);

    /// <summary>
    /// Soft-disables one active encrypted value.
    /// </summary>
    Task<bool> DisableAsync(
        string organizationId,
        string id,
        DateTimeOffset disabledAtUtc,
        CancellationToken cancellationToken
    );
}
