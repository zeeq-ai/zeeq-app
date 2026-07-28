using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Store for repository-scoped MCP prompt activation and placeholder overrides.
/// </summary>
/// <remarks>
/// Two very different callers share this store, which is why the read methods are narrow rather
/// than a single general query:
///
/// <list type="bullet">
/// <item><description>
/// The MCP retrieval path (<c>DynamicPromptsService</c>) needs exactly one active row for one
/// prompt, on a latency-sensitive request. <see cref="FindActiveForPromptAsync" /> encodes the
/// activation rule so no caller can accidentally apply an inactive repository's values.
/// </description></item>
/// <item><description>
/// The repository configuration UI needs every row for a repository, including inactive ones, so a
/// user can see and toggle what is available. That is <see cref="ListForRepositoryAsync" />.
/// </description></item>
/// </list>
/// </remarks>
public interface ICodeRepositoryPromptConfigurationStore
{
    /// <summary>
    /// Lists every prompt configuration row for one repository, active or not.
    /// </summary>
    /// <remarks>
    /// Used by the repository configuration surface, which renders one accordion per available
    /// organization prompt and needs saved values for prompts the user has since deactivated.
    /// </remarks>
    /// <param name="organizationId">Owning organization; also the distribution key.</param>
    /// <param name="repositoryId">Local Zeeq repository mapping id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Configuration rows for the repository; empty when none were ever saved.</returns>
    Task<IReadOnlyList<CodeRepositoryPromptConfiguration>> ListForRepositoryAsync(
        string organizationId,
        string repositoryId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Finds the active configuration for one prompt document in one repository.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="null" /> both when no row exists and when the row is inactive, because
    /// the retrieval path treats those identically: render authored defaults. Keeping the activation
    /// predicate inside the store means the substitution caller cannot get that rule wrong.
    /// </remarks>
    /// <param name="organizationId">Owning organization; also the distribution key.</param>
    /// <param name="repositoryId">Local Zeeq repository mapping id.</param>
    /// <param name="libraryId">Library containing the prompt document.</param>
    /// <param name="documentId">Stable prompt document identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The active configuration, or <see langword="null" /> to fall back to defaults.</returns>
    Task<CodeRepositoryPromptConfiguration?> FindActiveForPromptAsync(
        string organizationId,
        string repositoryId,
        string libraryId,
        string documentId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Creates or replaces the configuration for one repository/prompt pair.
    /// </summary>
    /// <remarks>
    /// Implementations must upsert on the natural key (organization, repository, library, document)
    /// rather than the synthetic id, so a save from the UI does not depend on the client knowing
    /// whether a row already exists.
    /// </remarks>
    /// <param name="configuration">Desired state, including activation and placeholder values.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted row, including its assigned id and refreshed timestamps.</returns>
    Task<CodeRepositoryPromptConfiguration> UpsertAsync(
        CodeRepositoryPromptConfiguration configuration,
        CancellationToken cancellationToken
    );
}
