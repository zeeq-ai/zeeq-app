using Zeeq.Core.Documents;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews; // ICodeRepositoryStore lives in Zeeq.Core.Models.dll

namespace Zeeq.Platform.Documents;

/// <summary>
/// Replaces the full set of repository mappings for a library.
/// </summary>
/// <remarks>
/// This is a set-replace operation: every configured repository in the
/// organization is evaluated and only those whose <see cref="CodeRepository.LibraryIds"/>
/// actually changed are written back via <see cref="ICodeRepositoryStore.UpsertAsync"/>.
/// </remarks>
public sealed class UpdateLibraryRepositoryMappingsHandler(
    ILibraryDocumentStore libraries,
    ICodeRepositoryStore repositories
) : IEndpointHandler
{
    /// <summary>
    /// Handles the update-repository-mappings request.
    /// </summary>
    public async Task<
        Results<Ok<LibraryRepositoryMappingsResponse>, BadRequest<LibraryError>, NotFound>
    > HandleAsync(
        string orgId,
        string name,
        UpdateLibraryRepositoryMappingsRequest request,
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(orgId))
        {
            return TypedResults.BadRequest(new LibraryError("Active organization is required."));
        }

        var library = await libraries.GetLibraryAsync(orgId, name, ct);
        if (library is null)
        {
            return TypedResults.NotFound();
        }

        var configuredRepos = await repositories.ListConfiguredForOrganizationAsync(orgId, ct);
        var repoById = configuredRepos.ToDictionary(r => r.Id, StringComparer.Ordinal);

        var unknownIds = request.RepositoryIds.Where(id => !repoById.ContainsKey(id)).ToArray();
        if (unknownIds.Length > 0)
        {
            return TypedResults.BadRequest(
                new LibraryError(
                    $"Unknown or cross-organization repository IDs: {string.Join(", ", unknownIds)}."
                )
            );
        }

        var requestedSet = request.RepositoryIds.ToHashSet(StringComparer.Ordinal);

        foreach (var repo in configuredRepos)
        {
            var hasLibrary = repo.LibraryIds.Contains(library.Id, StringComparer.Ordinal);
            var shouldHave = requestedSet.Contains(repo.Id);

            if (hasLibrary == shouldHave)
            {
                continue;
            }

            var updatedIds = shouldHave
                ? [.. repo.LibraryIds, library.Id]
                : repo.LibraryIds.Where(id => id != library.Id).ToArray();

            repo.LibraryIds = updatedIds;
            repo.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await repositories.UpsertAsync(repo, ct);
        }

        return TypedResults.Ok(
            new LibraryRepositoryMappingsResponse(library.Id, [.. requestedSet])
        );
    }
}
