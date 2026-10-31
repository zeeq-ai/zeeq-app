using Microsoft.EntityFrameworkCore;
using Zeeq.Core.Documents;

namespace Zeeq.Data.Postgres.Documents;

// Path/alias lookup helpers; preserve the ordered fallback rules independently of audit capture.
internal sealed partial class PostgresLibraryDocumentStore
{
    private static string ReverseString(string value) => new([.. value.Reverse()]);

    private Task<LibraryDocument?> FindByExactPathAsync(
        string organizationId,
        string libraryId,
        string path,
        CancellationToken ct
    ) =>
        db
            .LibraryDocuments.AsNoTracking()
            .TagWithOperationCallSite("documents.library_document.get_by_exact_path")
            // FirstOrDefault, not SingleOrDefault: path is only a unique identity for
            // hand-authored/repository documents (enforced by the partial unique index that
            // excludes rows with a SourceExternalId). External-source (Notion) documents can
            // legitimately share a resolved breadcrumb path, so a SingleOrDefaultAsync here
            // would throw on that collision instead of returning a reasonable match.
            .FirstOrDefaultAsync(
                document =>
                    document.OrganizationId == organizationId
                    && document.LibraryId == libraryId
                    && document.Path == path,
                ct
            );

    /// <summary>
    /// Finds the best suffix match across all candidates in a single round trip.
    /// </summary>
    /// <remarks>
    /// Candidates are ordered most-specific-first by <see cref="BuildPathCandidates"/>.
    /// <c>UNNEST … WITH ORDINALITY</c> preserves that order as <c>c.idx</c>, so
    /// <c>ORDER BY c.idx, d.path</c> returns the most-specific match first; path is the
    /// deterministic tie-break when multiple documents match the same candidate.
    ///
    /// Raw SQL is required here because EF Core LINQ cannot express an
    /// <c>UNNEST … WITH ORDINALITY</c> join, which is the only way to fan out and
    /// rank multiple LIKE patterns against the reversed-path index in one query.
    /// </remarks>
    private Task<LibraryDocument?> FindByAnySuffixAsync(
        string organizationId,
        string libraryId,
        string[] candidates,
        CancellationToken ct
    )
    {
        var reversedPatterns = Array.ConvertAll(candidates, static c => Reverse(c) + "%");

        return db
            .LibraryDocuments.FromSql(
                $"""
                SELECT d.*
                FROM zeeq.docs_library_documents d
                JOIN UNNEST({reversedPatterns}::text[]) WITH ORDINALITY AS c(pattern, idx)
                  ON d.path_reversed LIKE c.pattern
                WHERE d.organization_id = {organizationId}
                  AND d.library_id = {libraryId}
                ORDER BY c.idx, d.path
                """
            )
            .AsNoTracking()
            .TagWithOperationCallSite("documents.library_document.get_by_suffix_path_batched")
            .FirstOrDefaultAsync(ct);
    }

    private static string[] BuildPathCandidates(string input)
    {
        var parts = input.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return [];
        }

        var candidates = new string[parts.Length];
        for (var start = 0; start < parts.Length; start++)
        {
            candidates[start] = "/" + string.Join('/', parts[start..]);
        }

        return candidates;
    }

    private static string NormalizeLookupPath(string path)
    {
        var normalized = path.Trim().Replace('\\', '/').ToLowerInvariant();
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        normalized = "/" + string.Join('/', parts);

        return normalized.EndsWith(".md", StringComparison.Ordinal)
            ? normalized
            : normalized + ".md";
    }

    private static string Reverse(string value) => new(value.Reverse().ToArray());

    /// <summary>
    /// Finds a document by an exact match against a recorded previous path (alias).
    /// This is the final fallback in <see cref="GetByPathAsync"/> after the exact-path
    /// and suffix-path tiers both miss, so renamed documents still resolve by old paths (D-4).
    /// </summary>
    private Task<LibraryDocument?> FindByPreviousPathAsync(
        string organizationId,
        string libraryId,
        string path,
        CancellationToken ct
    ) =>
        db
            .LibraryDocuments.AsNoTracking()
            .TagWithOperationCallSite("documents.library_document.get_by_previous_path")
            .Where(d =>
                d.OrganizationId == organizationId
                && d.LibraryId == libraryId
                && d.PreviousPaths.Contains(path)
            )
            .FirstOrDefaultAsync(ct);
}
