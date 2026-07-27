using Zeeq.Core.Documents;

namespace Zeeq.Platform.Documents;

/// <summary>
/// Sets or clears a document's organization-scoped skill status.
/// </summary>
/// <remarks>
/// The stored enum intentionally has additional future scopes, but this endpoint only accepts
/// <see cref="LibraryDocumentScopedSkill.None"/> and
/// <see cref="LibraryDocumentScopedSkill.Organization"/> while the product exposes a binary toggle.
/// </remarks>
public sealed class SetDocumentScopedSkillHandler(ILibraryDocumentStore store) : IEndpointHandler
{
    /// <summary>
    /// Handles the set scoped-skill request.
    /// </summary>
    /// <param name="orgId">Organization ID from the route.</param>
    /// <param name="name">Library name.</param>
    /// <param name="request">The document id and desired scoped-skill state.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated document summary or a 400/404.</returns>
    public async Task<
        Results<Ok<DocumentResponse>, BadRequest<DocumentError>, NotFound>
    > HandleAsync(
        string orgId,
        string name,
        SetDocumentScopedSkillRequest request,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(request.DocumentId))
        {
            return TypedResults.BadRequest(new DocumentError("documentId is required."));
        }

        if (
            request.AsScopedSkill
            is not LibraryDocumentScopedSkill.None
                and not LibraryDocumentScopedSkill.Organization
        )
        {
            return TypedResults.BadRequest(
                new DocumentError("Only None and Organization scoped-skill values are supported.")
            );
        }

        var context = await DocumentEndpointContext.ResolveAsync(store, orgId, name, ct);
        if (context.Problem is not null)
        {
            return context.Problem.Kind == DocumentEndpointProblemKind.NotFound
                ? TypedResults.NotFound()
                : TypedResults.BadRequest(new DocumentError(context.Problem.Message!));
        }

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

        var updated = await store.SetScopedSkillAsync(
            context.OrganizationId,
            context.Library.Id,
            document.Id,
            request.AsScopedSkill,
            ct
        );

        return updated is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(LibraryEndpointMapping.ToResponse(updated));
    }
}
