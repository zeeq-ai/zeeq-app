using Zeeq.Core.Documents;

namespace Zeeq.Platform.Documents;

/// <summary>
/// Sets or clears a document's code-review exclusion flag.
/// </summary>
/// <remarks>
/// <para>
/// Excluded documents are operational/informational content (runbooks, CLI cheatsheets) that
/// code-review agents must not consult: the document stores hide them from list and search
/// results on the code-review execution path only (see <see cref="DocumentSearchScope"/>).
/// Interactive MCP and HTTP callers still see them, and direct reads by path always resolve.
/// </para>
/// <para>
/// Synced/remote documents are rejected with a 400 (v1 scope): a sync run owns their lifecycle
/// — <see cref="ILibraryDocumentStore.UpsertSyncedDocumentAsync"/> move-detection can rewrite
/// rows, and the shared public tables have no per-org override — so the flag is only meaningful
/// on hand-authored documents. The editor hides the toggle for remote documents; this guard
/// keeps direct API callers honest too.
/// </para>
/// </remarks>
public sealed class SetDocumentReviewExclusionHandler(ILibraryDocumentStore store)
    : IEndpointHandler
{
    /// <summary>
    /// Handles the set review exclusion request.
    /// </summary>
    /// <param name="orgId">Organization ID from the route.</param>
    /// <param name="name">Library name.</param>
    /// <param name="request">The document id and desired exclusion state.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated document summary or a 400/404.</returns>
    public async Task<
        Results<Ok<DocumentResponse>, BadRequest<DocumentError>, NotFound>
    > HandleAsync(
        string orgId,
        string name,
        SetDocumentReviewExclusionRequest request,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(request.DocumentId))
        {
            return TypedResults.BadRequest(new DocumentError("documentId is required."));
        }

        var context = await DocumentEndpointContext.ResolveAsync(store, orgId, name, ct);
        if (context.Problem is not null)
        {
            return context.Problem.Kind == DocumentEndpointProblemKind.NotFound
                ? TypedResults.NotFound()
                : TypedResults.BadRequest(new DocumentError(context.Problem.Message!));
        }

        // Resolve the exact loaded document so the synced-document guard and the mutation
        // target the same stable row. Path/suffix/alias resolution belongs to read flows;
        // this toggle is only exposed from the editor for an already loaded document.
        var document = await store.GetByIdAsync(
            context.OrganizationId,
            context.Library!.Id,
            request.DocumentId,
            ct
        );

        if (document is null)
        {
            return TypedResults.NotFound();
        }

        if (document.SyncRunId is not null || document.SourceOrigin is not null)
        {
            return TypedResults.BadRequest(
                new DocumentError(
                    "Synced documents cannot be excluded from code reviews; the flag is only supported on hand-authored documents."
                )
            );
        }

        var updated = await store.SetCodeReviewExclusionAsync(
            context.OrganizationId,
            context.Library.Id,
            document.Id,
            request.Excluded,
            ct
        );

        // The document resolved a moment ago; a concurrent delete between the read and the
        // narrow update is the only way to land here.
        return updated is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(LibraryEndpointMapping.ToResponse(updated));
    }
}
