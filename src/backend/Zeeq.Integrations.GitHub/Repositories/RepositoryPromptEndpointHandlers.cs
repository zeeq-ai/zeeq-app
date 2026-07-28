using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Zeeq.Core.Common.AspNetCore.Contracts;
using Zeeq.Core.Documents;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Integrations.GitHub;

/// <summary>
/// Shared helpers for the repository prompt configuration endpoints.
/// </summary>
/// <remarks>
/// These endpoints are reachable by any member of the active organization, unlike the repository
/// management endpoints alongside them. Every handler therefore re-resolves the repository inside the
/// caller's organization before touching prompt state — the route id alone is never trusted.
/// </remarks>
internal static class RepositoryPromptEndpointSupport
{
    /// <summary>Upper bound on one placeholder value, in characters.</summary>
    /// <remarks>
    /// Bounds the rendered prompt against a pathological configuration and keeps a single repository
    /// from producing a prompt body large enough to blow past the render cache's payload limit.
    /// </remarks>
    internal const int MaximumValueLength = 8_000;

    /// <summary>Upper bound on how many placeholder values one prompt may store.</summary>
    internal const int MaximumValueCount = 100;

    /// <summary>
    /// Full tenant-scoped prompt identity used when joining saved configuration to prompt documents.
    /// </summary>
    /// <remarks>
    /// <c>document_id</c> is only unique inside a library and organization. Keeping
    /// <c>organization_id</c> in this in-memory key mirrors the database distribution key and makes
    /// call-path reviews line up with the persisted natural key.
    /// </remarks>
    internal readonly record struct PromptConfigurationKey(
        string OrganizationId,
        string LibraryId,
        string DocumentId
    );

    /// <summary>
    /// Prompt description shown in the configuration UI.
    /// </summary>
    /// <remarks>
    /// Mirrors the precedence the MCP prompt list uses — manual override, then parsed front matter,
    /// then title — so the configuration surface labels a prompt the same way an agent sees it.
    /// </remarks>
    internal static string Description(LibraryScopedSkillDocument document) =>
        !string.IsNullOrWhiteSpace(document.ManualSkillDescription)
            ? document.ManualSkillDescription
        : !string.IsNullOrWhiteSpace(document.ParsedSkillDescription)
            ? document.ParsedSkillDescription
        : document.Title ?? string.Empty;

    /// <summary>
    /// Builds the prompt detail response shared by read and save endpoints.
    /// </summary>
    internal static RepositoryPromptDetailResponse ToDetailResponse(
        LibraryScopedSkillDocument document,
        bool active,
        IReadOnlyDictionary<string, string>? values
    )
    {
        // Shape comes from the document, values come from config. Parsing live means a placeholder
        // added or removed in the prompt shows up here immediately, with no migration of saved rows.
        var placeholders = PromptPlaceholderParser.Parse(document.Content);

        return new RepositoryPromptDetailResponse(
            DocumentId: document.DocumentId,
            LibraryId: document.LibraryId,
            LibraryName: document.LibraryName,
            Path: document.Path,
            Title: document.Title,
            Description: Description(document),
            Active: active,
            Placeholders:
            [
                .. placeholders.Select(placeholder => new RepositoryPromptPlaceholderResponse(
                    Name: placeholder.Name,
                    Label: placeholder.Label,
                    Description: placeholder.Description,
                    DefaultValue: placeholder.DefaultValue,
                    // NOTE: A missing value means "use the authored default"; an existing empty
                    // string means "render nothing intentionally". Keep TryGetValue so the response
                    // preserves that distinction for the UI and save path.
                    Value: values is not null && values.TryGetValue(placeholder.Name, out var value)
                        ? value
                        : null
                )),
            ]
        );
    }

    /// <summary>
    /// Resolves a configured repository by local id, including paused review mappings.
    /// </summary>
    internal static Task<CodeRepository?> FindConfigurableRepositoryAsync(
        ICodeRepositoryStore repositories,
        string organizationId,
        string repositoryId,
        CancellationToken cancellationToken
    )
    {
        // NOTE: The store method name predates paused-repository configuration, but its contract is
        // "non-soft-disabled by local id" and intentionally does not filter Enabled. These endpoints
        // configure mappings for future MCP/review behavior; pausing webhook reviews must not hide
        // prompt or library settings.
        return repositories.FindActiveForOrganizationAsync(
            organizationId,
            repositoryId,
            cancellationToken
        );
    }

    /// <summary>
    /// Validates a submitted placeholder value set.
    /// </summary>
    /// <returns>An error message, or <see langword="null" /> when the values are acceptable.</returns>
    internal static string? ValidateValues(Dictionary<string, string>? values)
    {
        if (values is null)
        {
            return null;
        }

        if (values.Count > MaximumValueCount)
        {
            return $"A prompt may define at most {MaximumValueCount} placeholder values.";
        }

        foreach (var (name, value) in values)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "Placeholder names cannot be blank.";
            }

            if (value is null)
            {
                return $"Placeholder '{name}' cannot be null.";
            }

            if (value.Length > MaximumValueLength)
            {
                return $"Placeholder '{name}' exceeds the {MaximumValueLength} character limit.";
            }
        }

        return null;
    }
}

/// <summary>
/// Lists the organization prompts available to a repository, with its activation state.
/// </summary>
/// <remarks>
/// The response joins two independent sets: every organization-scoped skill document in the tenant,
/// and the rows this repository has saved. A prompt with no saved row is returned as inactive with no
/// configured values, so the UI can render the full catalog from one call.
/// </remarks>
public sealed class ListRepositoryPromptsHandler(
    ICodeRepositoryStore repositories,
    ICodeRepositoryPromptConfigurationStore promptConfigurations,
    ILibraryDocumentStore documents
) : IEndpointHandler
{
    /// <summary>
    /// Returns one summary row per organization prompt for the route repository.
    /// </summary>
    public async Task<IResult> HandleAsync(
        string repositoryId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        // Tenant scope comes from the session cookie, never the route. The route only names which
        // repository within that tenant is being configured.
        var organizationId = user.AsZeeqMinimalIdentity().OrganizationId;
        if (string.IsNullOrWhiteSpace(organizationId))
        {
            return Results.Unauthorized();
        }

        // Doubles as the authorization check: resolving inside the caller's organization is what
        // proves this repository is theirs. A guessed id from another tenant simply misses.
        var repository = await RepositoryPromptEndpointSupport.FindConfigurableRepositoryAsync(
            repositories,
            organizationId,
            repositoryId,
            cancellationToken
        );
        if (repository is null)
        {
            return Results.NotFound(
                new GitHubRepositoryManagementError("Repository mapping was not found.")
            );
        }

        // Two independent reads: the tenant-wide prompt catalog, and what this one repository has
        // saved against it. Neither knows about the other until they are joined below.
        var prompts = await documents.ListScopedSkillDocumentsAsync(
            organizationId,
            LibraryDocumentScopedSkill.Organization,
            cancellationToken
        );
        var configurations = await promptConfigurations.ListForRepositoryAsync(
            organizationId,
            repositoryId,
            cancellationToken
        );

        var byPrompt = configurations.ToDictionary(
            configuration => new RepositoryPromptEndpointSupport.PromptConfigurationKey(
                configuration.OrganizationId,
                configuration.LibraryId,
                configuration.DocumentId
            )
        );

        // Left join with the catalog driving: a prompt this repository never configured still ships
        // as inactive with zero values, so the UI renders the complete catalog from one call.
        return Results.Ok(
            prompts
                .Select(prompt =>
                {
                    // NOTE: The database natural key is organization/repository/library/document.
                    // This join has no repository dimension because ListForRepositoryAsync already
                    // scoped the rows to this repository, but it keeps organization_id plus the full
                    // prompt identity so same document ids in different libraries cannot collide.
                    byPrompt.TryGetValue(
                        new RepositoryPromptEndpointSupport.PromptConfigurationKey(
                            organizationId,
                            prompt.LibraryId,
                            prompt.DocumentId
                        ),
                        out var configuration
                    );

                    return new RepositoryPromptSummaryResponse(
                        DocumentId: prompt.DocumentId,
                        LibraryId: prompt.LibraryId,
                        LibraryName: prompt.LibraryName,
                        Path: prompt.Path,
                        Title: prompt.Title,
                        Description: RepositoryPromptEndpointSupport.Description(prompt),
                        Active: configuration?.Active ?? false,
                        ConfiguredValueCount: configuration?.PlaceholderValues.Count ?? 0
                    );
                })
                .ToArray()
        );
    }
}

/// <summary>
/// Returns one prompt's declared placeholders merged with the repository's saved values.
/// </summary>
/// <remarks>
/// Placeholders are parsed from the live document body on every call rather than cached, so adding or
/// removing a placeholder in the prompt is reflected the next time a user expands it.
/// </remarks>
public sealed class GetRepositoryPromptHandler(
    ICodeRepositoryStore repositories,
    ICodeRepositoryPromptConfigurationStore promptConfigurations,
    ILibraryDocumentStore documents
) : IEndpointHandler
{
    /// <summary>
    /// Returns the prompt detail, or 404 when the repository or prompt does not resolve.
    /// </summary>
    public async Task<IResult> HandleAsync(
        string repositoryId,
        string documentId,
        string libraryId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var organizationId = user.AsZeeqMinimalIdentity().OrganizationId;
        if (string.IsNullOrWhiteSpace(organizationId))
        {
            return Results.Unauthorized();
        }

        // A prompt is identified by (library, document); the id in the route is only half of that.
        if (string.IsNullOrWhiteSpace(libraryId))
        {
            return Results.BadRequest(
                new GitHubRepositoryManagementError("libraryId is required.")
            );
        }

        // Same organization-scoped resolve as the list endpoint — the repository must be the
        // caller's before any of its prompt state is read.
        var repository = await RepositoryPromptEndpointSupport.FindConfigurableRepositoryAsync(
            repositories,
            organizationId,
            repositoryId,
            cancellationToken
        );
        if (repository is null)
        {
            return Results.NotFound(
                new GitHubRepositoryManagementError("Repository mapping was not found.")
            );
        }

        // The document read is scoped to Organization skills, so a document that exists but was
        // never published as a prompt is indistinguishable from one that does not exist.
        var document = await documents.GetScopedSkillDocumentAsync(
            organizationId,
            libraryId,
            documentId,
            LibraryDocumentScopedSkill.Organization,
            cancellationToken
        );
        if (document is null)
        {
            return Results.NotFound(
                new GitHubRepositoryManagementError("Organization prompt was not found.")
            );
        }

        // Read the row directly rather than through the active-only lookup: the UI must show saved
        // values for a prompt the user has deactivated, otherwise reactivating would look like it
        // lost their input.
        var configurations = await promptConfigurations.ListForRepositoryAsync(
            organizationId,
            repositoryId,
            cancellationToken
        );
        var configuration = configurations.FirstOrDefault(row =>
            string.Equals(row.DocumentId, documentId, StringComparison.Ordinal)
            && string.Equals(row.LibraryId, libraryId, StringComparison.Ordinal)
        );

        // Merge: the parser supplies name/label/default, the configuration supplies the override.
        // Placeholders drive the loop, so a saved value whose name no longer exists is dropped from
        // the response but left untouched in storage — a rename never destroys the user's input.
        return Results.Ok(
            RepositoryPromptEndpointSupport.ToDetailResponse(
                document,
                configuration?.Active ?? false,
                configuration?.PlaceholderValues
            )
        );
    }
}

/// <summary>
/// Saves a repository's activation state and placeholder values for one prompt.
/// </summary>
public sealed class SaveRepositoryPromptHandler(
    ICodeRepositoryStore repositories,
    ICodeRepositoryPromptConfigurationStore promptConfigurations,
    ILibraryDocumentStore documents
) : IEndpointHandler
{
    /// <summary>
    /// Replaces the stored configuration for the route repository and prompt.
    /// </summary>
    public async Task<IResult> HandleAsync(
        string repositoryId,
        string documentId,
        SaveRepositoryPromptRequest request,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var organizationId = user.AsZeeqMinimalIdentity().OrganizationId;
        if (string.IsNullOrWhiteSpace(organizationId))
        {
            return Results.Unauthorized();
        }

        // Payload shape is checked before any I/O so an oversized or malformed body costs no queries.
        var validationError = RepositoryPromptEndpointSupport.ValidateValues(request.Values);
        if (validationError is not null)
        {
            return Results.BadRequest(new GitHubRepositoryManagementError(validationError));
        }

        var repository = await RepositoryPromptEndpointSupport.FindConfigurableRepositoryAsync(
            repositories,
            organizationId,
            repositoryId,
            cancellationToken
        );
        if (repository is null)
        {
            return Results.NotFound(
                new GitHubRepositoryManagementError("Repository mapping was not found.")
            );
        }

        // Confirm the prompt is a real organization-scoped document in this tenant before storing a
        // row against it. Without this, a caller could seed configuration for arbitrary ids.
        var document = await documents.GetScopedSkillDocumentAsync(
            organizationId,
            request.LibraryId,
            documentId,
            LibraryDocumentScopedSkill.Organization,
            cancellationToken
        );
        if (document is null)
        {
            return Results.NotFound(
                new GitHubRepositoryManagementError("Organization prompt was not found.")
            );
        }

        // Ordinal comparer is required by the substitution path's span lookup; building it here
        // means the value arrives correctly compared even before it round-trips through storage.
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in request.Values ?? [])
        {
            values[name.Trim()] = value;
        }

        // Upsert keys on (organization, repository, library, document), so the client never has to
        // know whether a row already exists. Id is ignored on the create path and the team is
        // inherited from the repository rather than trusted from the request.
        var saved = await promptConfigurations.UpsertAsync(
            new CodeRepositoryPromptConfiguration
            {
                Id = string.Empty,
                OrganizationId = organizationId,
                TeamId = repository.TeamId,
                RepositoryId = repositoryId,
                LibraryId = request.LibraryId,
                DocumentId = documentId,
                Active = request.Active,
                PlaceholderValues = values,
            },
            cancellationToken
        );

        // Echo the same merged shape the GET returns, built from what was actually persisted, so the
        // client can patch its state from the save response without a follow-up read.
        return Results.Ok(
            RepositoryPromptEndpointSupport.ToDetailResponse(
                document,
                saved.Active,
                saved.PlaceholderValues
            )
        );
    }
}

/// <summary>
/// Replaces only the library mapping for a repository.
/// </summary>
/// <remarks>
/// This exists so library mapping can be edited by any organization member. The general repository
/// update endpoint also carries <c>Enabled</c>, <c>DisplayName</c>, and <c>TeamId</c>, so it is
/// restricted to owners and admins; a request shape that cannot express those fields is what makes
/// the looser authorization safe here.
/// </remarks>
public sealed class UpdateRepositoryLibrariesHandler(
    ICodeRepositoryStore repositories,
    ILibraryDocumentStore libraries
) : IEndpointHandler
{
    /// <summary>
    /// Sets the repository's mapped libraries, leaving every other setting untouched.
    /// </summary>
    public async Task<IResult> HandleAsync(
        string repositoryId,
        UpdateRepositoryLibrariesRequest request,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var organizationId = user.AsZeeqMinimalIdentity().OrganizationId;
        if (string.IsNullOrWhiteSpace(organizationId))
        {
            return Results.Unauthorized();
        }

        var repository = await RepositoryPromptEndpointSupport.FindConfigurableRepositoryAsync(
            repositories,
            organizationId,
            repositoryId,
            cancellationToken
        );
        if (repository is null)
        {
            return Results.NotFound(
                new GitHubRepositoryManagementError("Repository mapping was not found.")
            );
        }

        // Replace-wholesale semantics: an empty array unmaps everything. Ids are checked against the
        // caller's organization so a mapping cannot reference another tenant's library.
        var libraryIds = request.LibraryIds ?? [];
        if (libraryIds.Length > 0)
        {
            var validationError = await GitHubRepositoryEndpointSupport.ValidateLibraryIdsAsync(
                libraryIds,
                organizationId,
                libraries,
                cancellationToken
            );
            if (validationError is not null)
            {
                return Results.BadRequest(new GitHubRepositoryManagementError(validationError));
            }
        }

        // The loaded entity is round-tripped with only LibraryIds touched, so Enabled, DisplayName,
        // and TeamId carry through unchanged — that preservation is what keeps this route safe for
        // non-admin members, and it is pinned by a test.
        repository.LibraryIds = libraryIds;
        repository.UpdatedAtUtc = DateTimeOffset.UtcNow;

        var saved = await repositories.UpsertAsync(repository, cancellationToken);

        return Results.Ok(GitHubRepositoryEndpointSupport.ToResponse(saved));
    }
}
