using Zeeq.Core.Documents;
using Zeeq.Core.Identity;

namespace Zeeq.Platform.Documents;

/// <summary>
/// Updates a library in the caller's active organization.
/// </summary>
public sealed class UpdateLibraryHandler(
    ILibraryDocumentStore store,
    IDocsPublicSourceStore publicSources,
    ILibraryVisibilityStore visibility
) : IEndpointHandler
{
    /// <summary>
    /// Handles the update library request.
    /// </summary>
    public async Task<
        Results<
            Ok<LibraryResponse>,
            BadRequest<LibraryError>,
            NotFound,
            ForbidHttpResult,
            Conflict<LibraryError>
        >
    > HandleAsync(
        string orgId,
        string name,
        UpdateLibraryRequest request,
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(orgId))
        {
            return TypedResults.BadRequest(new LibraryError("Active organization is required."));
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return TypedResults.BadRequest(new LibraryError("Library name is required."));
        }

        var newName = request.Name.Trim();
        if (!LibraryNameValidator.IsRouteSafe(newName))
        {
            return TypedResults.BadRequest(
                new LibraryError(
                    "Library name must contain only letters, numbers, hyphens, and underscores."
                )
            );
        }

        if (
            request.Visibility is not null
            && user.GetLibraryAudience(orgId)?.IsAdministrator != true
        )
            return TypedResults.Forbid();

        var existing = await store.GetLibraryAsync(orgId, name, ct);
        if (existing is null)
        {
            return TypedResults.NotFound();
        }

        var nextFullResyncAt =
            request.RunFullResync && existing.SourceKind == RepositorySourceKind.Notion.ToString()
                ? DateTimeOffset.UtcNow
                : existing.NextFullResyncAt;
        var update = new Library
        {
            Id = existing.Id,
            OrganizationId = existing.OrganizationId,
            TeamId = existing.TeamId,
            Name = newName,
            Description = request.Description ?? existing.Description,
            PublicSourceId = existing.PublicSourceId,
            IncludeFilters = request.IncludeFilters ?? existing.IncludeFilters,
            ExcludeFilters = request.ExcludeFilters ?? existing.ExcludeFilters,
            SourceKind = existing.SourceKind,
            SourceRepoUrl = existing.SourceRepoUrl,
            SourceSyncedAt = existing.SourceSyncedAt,
            SourceDefaultIncludeFilters = existing.SourceDefaultIncludeFilters,
            SourceDefaultExcludeFilters = existing.SourceDefaultExcludeFilters,
            SyncStatus = existing.SyncStatus,
            NextSyncAt = existing.NextSyncAt,
            NextFullResyncAt = nextFullResyncAt,
            ExternalSource = existing.ExternalSource,
            ActiveSyncRunId = existing.ActiveSyncRunId,
            ActiveSyncRunCreatedAtUtc = existing.ActiveSyncRunCreatedAtUtc,
            SyncQueuedAtUtc = existing.SyncQueuedAtUtc,
            SyncStartedAtUtc = existing.SyncStartedAtUtc,
            ManualTriggerHistory = existing.ManualTriggerHistory,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        Library? updated;
        if (request.Visibility is { } change)
        {
            if (
                change.TeamIds is null
                || change.ExpectedTeamIds is null
                || change.TeamIds.Any(string.IsNullOrWhiteSpace)
                || change.ExpectedTeamIds.Any(string.IsNullOrWhiteSpace)
            )
                return TypedResults.BadRequest(new LibraryError("Invalid team selection."));

            var (status, result) = await visibility.SaveAsync(
                update,
                change,
                request.RunFullResync,
                ct
            );
            switch (status)
            {
                case LibraryVisibilitySaveStatus.Saved:
                    updated = result;
                    break;
                case LibraryVisibilitySaveStatus.Missing:
                    return TypedResults.NotFound();
                case LibraryVisibilitySaveStatus.Conflict:
                    return TypedResults.Conflict(
                        new LibraryError("Library visibility changed. Reopen Manage and try again.")
                    );
                case LibraryVisibilitySaveStatus.InvalidTeams:
                    return TypedResults.BadRequest(
                        new LibraryError("A selected team is no longer available.")
                    );
                case LibraryVisibilitySaveStatus.ConfirmationRequired:
                    return TypedResults.BadRequest(
                        new LibraryError("Confirm giving everyone in the organization access.")
                    );
                default:
                    throw new InvalidOperationException(
                        $"Unexpected library visibility status: {status}."
                    );
            }
        }
        else
        {
            updated = await store.TryUpdateLibraryAsync(update, request.RunFullResync, ct);
        }

        if (updated is null)
        {
            return TypedResults.NotFound();
        }

        var sourcesById = await LibraryEndpointMapping.LoadPublicSourceAsync(
            publicSources,
            updated,
            ct
        );
        return TypedResults.Ok(LibraryEndpointMapping.ToResponse(updated, sourcesById));
    }
}
