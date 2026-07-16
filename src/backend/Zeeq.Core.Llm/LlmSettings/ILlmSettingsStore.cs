using Zeeq.Core.Models;

namespace Zeeq.Core.Llm;

/// <summary>
/// Store for organization-level LLM tier configuration.
/// </summary>
/// <remarks>
/// The boundary intentionally exposes only the typed LLM configuration instead
/// of the full organization entity.
/// </remarks>
public interface ILlmSettingsStore
{
    /// <summary>
    /// Finds the LLM tier configuration for one organization.
    /// </summary>
    Task<OrganizationLlmConfiguration?> FindConfigurationAsync(
        string organizationId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Replaces the LLM tier configuration for one organization.
    /// </summary>
    Task<bool> UpdateConfigurationAsync(
        string organizationId,
        OrganizationLlmConfiguration configuration,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken
    );
}
