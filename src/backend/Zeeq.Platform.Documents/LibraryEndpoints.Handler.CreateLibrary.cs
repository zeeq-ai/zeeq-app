using Zeeq.Core.Documents;
using Zeeq.Core.Identity;
using Zeeq.Core.Llm;
using Zeeq.Core.Models;
using Zeeq.Integrations.Notion;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Platform.Documents;

/// <summary>
/// Creates a library in the caller's active organization — a plain
/// hand-authored library, or (when <see cref="CreateLibraryRequest.Source"/>
/// is set) a repository-sourced one.
/// </summary>
/// <remarks>
/// This does <b>not</b> queue the initial sync itself — <c>Zeeq.Platform.Documents</c>
/// cannot reference <c>Zeeq.Platform.Ingest</c> (a circular project
/// reference: Ingest → Integrations.GitHub → CodeReviews → Mcp.Documents →
/// Documents). The client calls <c>POST .../libraries/{name}/ingest-run</c>
/// (<c>TriggerLibraryIngestHandler</c>, extended to handle both private- and
/// public-source-backed libraries) as an immediate follow-up request right
/// after this one succeeds, for a repository-sourced library.
/// </remarks>
public sealed class CreateLibraryHandler(
    ILibraryDocumentStore store,
    IDocsPublicSourceStore publicSources,
    ICodeRepositoryStore repositories,
    IEncryptedValueStore encryptedValues,
    EncryptedValueEncryptionService encryption,
    IZeeqNotionClientFactory notionClients
) : IEndpointHandler
{
    /// <summary>
    /// Handles the create library request.
    /// </summary>
    public async Task<Results<Created<LibraryResponse>, BadRequest<LibraryError>>> HandleAsync(
        string orgId,
        CreateLibraryRequest request,
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        var teamId = user.AsZeeqMinimalIdentity().TeamId;
        if (string.IsNullOrWhiteSpace(orgId))
        {
            return TypedResults.BadRequest(new LibraryError("Active organization is required."));
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return TypedResults.BadRequest(new LibraryError("Library name is required."));
        }

        var name = request.Name.Trim();
        if (!LibraryNameValidator.IsRouteSafe(name))
        {
            return TypedResults.BadRequest(
                new LibraryError(
                    "Library name must contain only letters, numbers, hyphens, and underscores."
                )
            );
        }

        var libraryId = NewLibraryId();
        Library toCreate;
        DocsPublicSource? publicSource = null;
        string? storedNotionAccessTokenId = null;

        switch (request.Source)
        {
            case null:
                toCreate = LibraryBuilder
                    .ForLocal()
                    .Build(libraryId, orgId, name, teamId, request.Description);
                break;

            case { Kind: LibrarySourceKindRequest.Public }:
            {
                var validation = ValidatePublicSource(request.Source);
                if (validation is not null)
                {
                    return TypedResults.BadRequest(validation);
                }

                var normalizedUrl = NormalizeGitHubUrl(request.Source.RepoUrl!);
                publicSource = await publicSources.GetByRepoUrlAsync(normalizedUrl, ct);
                if (publicSource is null)
                {
                    var now = DateTimeOffset.UtcNow;
                    publicSource = await publicSources.CreateAsync(
                        new DocsPublicSource
                        {
                            Id = NewPublicSourceId(),
                            Kind = RepositorySourceKind.Public,
                            RepoUrl = normalizedUrl,
                            Name = DeriveNameFromUrl(normalizedUrl),
                            // Bootstrap defaults for future subscribers. This
                            // library's own effective filter is still governed
                            // by its own IncludeFilters/ExcludeFilters below,
                            // regardless of what these end up being changed to
                            // later by an admin or another subscriber.
                            DefaultIncludeFilters = request.Source.IncludeFilters,
                            DefaultExcludeFilters = request.Source.ExcludeFilters,
                            SyncStatus = "idle",
                            Status = "active",
                            NextSyncAt = now,
                            CreatedAt = now,
                            UpdatedAt = now,
                        },
                        ct
                    );
                }

                toCreate = LibraryBuilder
                    .ForPublicSource(
                        publicSource.Id,
                        request.Source.IncludeFilters,
                        request.Source.ExcludeFilters
                    )
                    .Build(libraryId, orgId, name, teamId, request.Description);
                break;
            }

            case { Kind: LibrarySourceKindRequest.Private }:
            {
                var validation = ValidatePrivateSource(request.Source);
                if (validation is not null)
                {
                    return TypedResults.BadRequest(validation);
                }

                var repository = await repositories.FindActiveForOrganizationAsync(
                    orgId,
                    request.Source.RepositoryId!,
                    ct
                );
                if (repository is null)
                {
                    // BadRequest (not NotFound): this is a create request, not a lookup —
                    // a stale/removed repository selection is an invalid *input*, and
                    // surfacing it as a LibraryError keeps the frontend's toast message
                    // specific instead of falling back to a generic failure message.
                    return TypedResults.BadRequest(
                        new LibraryError(
                            "The selected repository is no longer available. Refresh and pick again."
                        )
                    );
                }

                var repoUrl = $"https://github.com/{repository.OwnerQualifiedName}.git";
                toCreate = LibraryBuilder
                    .ForPrivateSource(
                        "GitHub",
                        repoUrl,
                        request.Source.IncludeFilters,
                        request.Source.ExcludeFilters
                    )
                    .Build(libraryId, orgId, name, teamId, request.Description);
                break;
            }

            case { Kind: LibrarySourceKindRequest.Notion }:
            {
                var validation = ValidateNotionSource(request.Source);
                if (validation is not null)
                {
                    return TypedResults.BadRequest(validation);
                }

                var accessToken = request.Source.AccessToken!.Trim();
                using var notionClient = notionClients.Create(accessToken);
                var identity = await notionClient.GetConnectionIdentityAsync(ct);
                if (identity is null)
                {
                    return TypedResults.BadRequest(
                        new LibraryError("Notion access token is invalid or unauthorized.")
                    );
                }

                var now = DateTimeOffset.UtcNow;
                var encryptedToken = await encryption.EncryptAsync(
                    orgId,
                    EncryptedValueKind.SecretString,
                    "Notion access token",
                    accessToken,
                    now,
                    ct
                );
                var storedToken = await encryptedValues.AddAsync(encryptedToken, ct);
                storedNotionAccessTokenId = storedToken.Id;

                toCreate = LibraryBuilder
                    .ForNotionSource(
                        new LibraryExternalSource
                        {
                            Notion = new NotionSourceConfiguration
                            {
                                AccessTokenValueId = storedToken.Id,
                                CallbackTokenSerial = 1,
                                ConnectionName = string.IsNullOrWhiteSpace(identity.Name)
                                    ? null
                                    : identity.Name.Trim(),
                            },
                        },
                        request.Source.IncludeFilters,
                        request.Source.ExcludeFilters
                    )
                    .Build(libraryId, orgId, name, teamId, request.Description);
                break;
            }

            default:
                return TypedResults.BadRequest(new LibraryError("Unknown library source kind."));
        }

        Library library;
        try
        {
            library = await store.CreateLibraryAsync(toCreate, ct);
        }
        catch when (storedNotionAccessTokenId is not null)
        {
            await TryDisableStoredNotionAccessTokenAsync(orgId, storedNotionAccessTokenId);
            throw;
        }

        var sourcesById = publicSource is null
            ? LibraryEndpointMapping.NoPublicSources
            : new Dictionary<string, DocsPublicSource> { [publicSource.Id] = publicSource };

        return TypedResults.Created(
            $"/api/v1/orgs/{Uri.EscapeDataString(orgId)}/libraries/{Uri.EscapeDataString(library.Name)}",
            LibraryEndpointMapping.ToResponse(library, sourcesById)
        );
    }

    private static LibraryError? ValidatePublicSource(CreateLibrarySourceRequest source)
    {
        if (!string.IsNullOrWhiteSpace(source.AccessToken))
        {
            return new LibraryError("Access token is only valid for a Notion source.");
        }

        if (string.IsNullOrWhiteSpace(source.RepoUrl))
        {
            return new LibraryError("A repository URL is required for a public source.");
        }

        if (!IsPlausibleGitHubUrl(source.RepoUrl))
        {
            return new LibraryError("Repository URL must look like https://github.com/owner/repo.");
        }

        return null;
    }

    private static LibraryError? ValidatePrivateSource(CreateLibrarySourceRequest source)
    {
        if (!string.IsNullOrWhiteSpace(source.AccessToken))
        {
            return new LibraryError("Access token is only valid for a Notion source.");
        }

        return string.IsNullOrWhiteSpace(source.RepositoryId)
            ? new LibraryError("A repository selection is required for a private source.")
            : null;
    }

    private static LibraryError? ValidateNotionSource(CreateLibrarySourceRequest source)
    {
        if (string.IsNullOrWhiteSpace(source.AccessToken))
        {
            return new LibraryError("A Notion access token is required.");
        }

        if (
            !string.IsNullOrWhiteSpace(source.RepoUrl)
            || !string.IsNullOrWhiteSpace(source.RepositoryId)
        )
        {
            return new LibraryError("Repository fields are not valid for a Notion source.");
        }

        return null;
    }

    private static bool IsPlausibleGitHubUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed)
        && parsed.Scheme == Uri.UriSchemeHttps
        && parsed.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && parsed.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Length
            >= 2;

    /// <summary>
    /// Normalizes a GitHub URL to a canonical clone URL: strips a trailing
    /// slash or <c>.git</c>, then re-appends <c>.git</c>, so
    /// <c>https://github.com/owner/repo</c> and
    /// <c>https://github.com/owner/repo.git</c> resolve to the same
    /// <see cref="DocsPublicSource"/> row.
    /// </summary>
    private static string NormalizeGitHubUrl(string url)
    {
        var trimmed = url.Trim().TrimEnd('/');
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }

        return trimmed + ".git";
    }

    private static string DeriveNameFromUrl(string normalizedUrl)
    {
        var withoutSuffix = normalizedUrl.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? normalizedUrl[..^4]
            : normalizedUrl;

        return withoutSuffix.Split('/', StringSplitOptions.RemoveEmptyEntries)[^1];
    }

    private static string NewLibraryId() => $"library_{Guid.CreateVersion7():N}";

    private static string NewPublicSourceId() => $"pubsrc_{Guid.CreateVersion7():N}";

    private async Task TryDisableStoredNotionAccessTokenAsync(
        string organizationId,
        string encryptedValueId
    )
    {
        try
        {
            await encryptedValues.DisableAsync(
                organizationId,
                encryptedValueId,
                DateTimeOffset.UtcNow,
                CancellationToken.None
            );
        }
        catch
        {
            // NOTE: Best-effort credential cleanup must not mask the original library-create
            // failure. Any still-active orphan can be disabled operationally by encrypted-value id.
        }
    }
}
