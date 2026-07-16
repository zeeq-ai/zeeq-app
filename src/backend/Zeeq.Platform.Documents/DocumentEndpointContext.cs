using Zeeq.Core.Documents;

namespace Zeeq.Platform.Documents;

internal sealed record DocumentEndpointContext(
    string OrganizationId,
    Library? Library,
    DocumentEndpointProblem? Problem
)
{
    public static async Task<DocumentEndpointContext> ResolveAsync(
        ILibraryDocumentStore store,
        string orgId,
        string name,
        CancellationToken ct
    )
    {
        // NOTE: tuple switch is intentional (user preference over sequential guard clauses); orgId is route-bound, not extracted from claims.
        var hasOrg = !string.IsNullOrWhiteSpace(orgId);
        var library =
            hasOrg && !string.IsNullOrWhiteSpace(name)
                ? await store.GetLibraryAsync(orgId, name, ct)
                : null;

        return (hasOrg, string.IsNullOrWhiteSpace(name), library) switch
        {
            (false, _, _) => new(
                string.Empty,
                null,
                new DocumentEndpointProblem(
                    DocumentEndpointProblemKind.BadRequest,
                    "Active organization is required."
                )
            ),
            (_, true, _) => new(
                orgId,
                null,
                new DocumentEndpointProblem(
                    DocumentEndpointProblemKind.BadRequest,
                    "Library name is required."
                )
            ),
            (_, _, null) => new(
                orgId,
                null,
                new DocumentEndpointProblem(DocumentEndpointProblemKind.NotFound)
            ),
            _ => new(orgId, library!, null),
        };
    }
}

internal sealed record DocumentEndpointProblem(
    DocumentEndpointProblemKind Kind,
    string? Message = null
);

internal enum DocumentEndpointProblemKind
{
    BadRequest,
    NotFound,
}
