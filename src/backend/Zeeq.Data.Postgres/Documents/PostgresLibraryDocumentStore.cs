using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;
using Zeeq.Core.Auditing;
using Zeeq.Core.Documents;
using Zeeq.Data.Postgres.Auditing;

namespace Zeeq.Data.Postgres.Documents;

/// <summary>
/// Postgres-backed store for document libraries and markdown documents.
/// </summary>
/// <remarks>
/// <paramref name="searchScope"/> gates the code-review exclusion filter: when the current DI
/// scope serves a code-review tool invocation (<see cref="DocumentSearchScope.ForCodeReviewExecution"/>),
/// <see cref="SearchAsync"/> and <see cref="ListDocumentsAsync"/> hide documents flagged
/// <see cref="LibraryDocument.ExcludedFromCodeReviews"/> or exposed as scoped skills. Path
/// resolution (<see cref="GetByPathAsync"/>) is intentionally unfiltered — an excluded document
/// must still resolve when read directly by path.
/// <para>
/// NOTE: the exclusion policy lives in each query's predicate rather than a centralized query
/// root (code review follow-up, 2026-07-15) — acceptable while only list/search consult it, but
/// any future list/search-shaped query added to this store (or the snippet store) must apply the
/// same <c>!ForCodeReviewExecution || (!ExcludedFromCodeReviews &amp;&amp; AsScopedSkill == None)</c>
/// predicate, or excluded documents leak back into review results.
/// </para>
/// <para>
/// Covered direct document writes and private-library sync mutations record their audit facts on
/// the same <see cref="PostgresDbContext"/> as the mutation. Library lifecycle/cascade deletion and
/// operational processing/lease stamps are not captured here. Capture is explicit, not an EF hook.
/// For covered changes the preimage is read from the database under
/// <c>FOR UPDATE</c>, never from a caller-mutated entity, and actual no-ops record nothing.
/// <paramref name="auditWriter"/> is resolved from DI at runtime; direct construction (tests)
/// falls back to a real writer so audit capture is never silently disabled.
/// </para>
/// <para>
/// Responsibility partials separate direct mutations, source reconciliation, operational sync state,
/// shared audit/locking policy, and path resolution. They are still one scoped store: keep the
/// constructor and field initialization here, and preserve transaction boundaries across parts.
/// </para>
/// </remarks>
internal sealed partial class PostgresLibraryDocumentStore(
    PostgresDbContext db,
    DocumentSearchScope searchScope,
    IAuditTrailWriter? auditWriter = null
) : ILibraryDocumentStore
{
    private readonly IAuditTrailWriter audit =
        auditWriter ?? new PostgresAuditTrailWriter(db, new AuditActorContext());

    private static readonly float[] SearchWeights = [0.1f, 0.2f, 0.4f, 1.0f];

    // Flag 4 normalizes for match density; flag 32 (rank/(rank+1)) bounds the score to [0, 1) so it
    // is directly comparable to the [0, 1] trigram similarity used by fuzzy title matching.
    private const NpgsqlTsRankingNormalization SearchNormalization =
        NpgsqlTsRankingNormalization.DivideByMeanHarmonicDistanceBetweenExtents
        | NpgsqlTsRankingNormalization.DivideByItselfPlusOne;

    /// <inheritdoc />
    public Task<Library?> GetLibraryAsync(
        string organizationId,
        string name,
        CancellationToken ct
    ) =>
        db
            .Libraries.TagWithOperationCallSite("documents.library.get")
            .SingleOrDefaultAsync(
                library => library.OrganizationId == organizationId && library.Name == name,
                ct
            );

    /// <inheritdoc />
    public Task<Library?> GetLibraryByIdAsync(
        string organizationId,
        string libraryId,
        CancellationToken ct
    ) =>
        db
            .Libraries.TagWithOperationCallSite("documents.library.get_by_id")
            .SingleOrDefaultAsync(
                library => library.OrganizationId == organizationId && library.Id == libraryId,
                ct
            );

    /// <inheritdoc />
    public async Task<IReadOnlyList<Library>> ListLibrariesAsync(
        string organizationId,
        CancellationToken ct
    ) =>
        await db
            .Libraries.TagWithOperationCallSite("documents.library.list")
            .Where(library => library.OrganizationId == organizationId)
            .OrderBy(library => library.Name)
            .ToArrayAsync(ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Library>> ListLibrariesByPublicSourceIdAsync(
        string publicSourceId,
        CancellationToken ct
    ) =>
        await db
            .Libraries.TagWithOperationCallSite("documents.library.list_by_public_source")
            .Where(library => library.PublicSourceId == publicSourceId)
            .ToArrayAsync(ct);

    /// <inheritdoc />
    public async Task<Library> CreateLibraryAsync(Library library, CancellationToken ct)
    {
        db.Libraries.Add(library);
        await db.SaveChangesAsync(ct);
        return library;
    }

    /// <inheritdoc />
    public async Task<Library> UpdateLibraryAsync(Library library, CancellationToken ct)
    {
        var existing = await db
            .Libraries.TagWithOperationCallSite("documents.library.update_find")
            .SingleAsync(
                row => row.OrganizationId == library.OrganizationId && row.Id == library.Id,
                ct
            );

        existing.Name = library.Name;
        existing.Description = library.Description;
        db.Entry(existing).Property(row => row.IncludeFilters).CurrentValue =
            library.IncludeFilters;
        db.Entry(existing).Property(row => row.ExcludeFilters).CurrentValue =
            library.ExcludeFilters;
        existing.UpdatedAt = library.UpdatedAt;

        await db.SaveChangesAsync(ct);
        return existing;
    }

    /// <inheritdoc />
    public async Task DeleteLibraryAsync(
        string organizationId,
        string libraryId,
        CancellationToken ct
    )
    {
        var library = await db
            .Libraries.TagWithOperationCallSite("documents.library.delete_find")
            .SingleOrDefaultAsync(
                row => row.OrganizationId == organizationId && row.Id == libraryId,
                ct
            );

        if (library is null)
        {
            return;
        }

        // Strip this library id out of every repository that still maps to it,
        // in the same SaveChangesAsync as the library delete (same DbContext, one
        // implicit transaction).
        var referencingRepositories = await db
            .CodeRepositories.TagWithOperationCallSite(
                "documents.library.delete_cascade_repositories"
            )
            .Where(r => r.OrganizationId == organizationId && r.LibraryIds.Contains(libraryId))
            .ToListAsync(ct);

        foreach (var repository in referencingRepositories)
        {
            repository.LibraryIds = repository.LibraryIds.Where(id => id != libraryId).ToArray();
        }

        db.Libraries.Remove(library);
        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<LibraryDocument?> GetByPathAsync(
        string organizationId,
        string libraryId,
        string input,
        CancellationToken ct
    )
    {
        var normalizedInput = NormalizeLookupPath(input);
        var candidates = BuildPathCandidates(normalizedInput);
        if (candidates.Length == 0)
        {
            return null;
        }

        var exactMatch = await FindByExactPathAsync(organizationId, libraryId, candidates[0], ct);
        if (exactMatch is not null)
        {
            return exactMatch;
        }

        var suffixMatch = await FindByAnySuffixAsync(organizationId, libraryId, candidates, ct);
        if (suffixMatch is not null)
        {
            return suffixMatch;
        }

        // Final fallback: exact match against a recorded previous path (alias) so
        // renamed documents still resolve by an old path (D-4).
        return await FindByPreviousPathAsync(organizationId, libraryId, normalizedInput, ct);
    }

    /// <inheritdoc />
    public async Task<LibraryDocument?> GetByIdAsync(
        string organizationId,
        string libraryId,
        string documentId,
        CancellationToken ct
    ) =>
        await db
            .LibraryDocuments.AsNoTracking()
            .TagWithOperationCallSite("documents.library_document.get_by_id")
            .FirstOrDefaultAsync(
                row =>
                    row.OrganizationId == organizationId
                    && row.LibraryId == libraryId
                    && row.Id == documentId,
                ct
            );

    /// <inheritdoc />
    public async Task<IReadOnlyList<LibraryDocumentMatch>> SearchAsync(
        string organizationId,
        string libraryId,
        string query,
        int limit,
        CancellationToken ct
    )
    {
        if (limit <= 0 || string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        // ToTsQuery is a translation-only EF function (like the WebSearchToTsQuery it replaced
        // here): it must stay inline inside the expression tree, so it is repeated rather than
        // hoisted into a local. orQueryText and the lowercased fuzzy term are ordinary values and
        // are safe to compute once. See ToOrQueryText's remarks for why raw OR-of-terms replaced
        // EF.Functions.WebSearchToTsQuery here.
        var fuzzyQuery = query.ToLowerInvariant();
        var orQueryText = ToOrQueryText(query);
        var excludeCodeReviewExcluded = searchScope.ForCodeReviewExecution;

        // The OR predicate lets Postgres BitmapOr the GIN index on search_vector with the trigram
        // GIN index on title_normalized, so both retrieval signals stay index-backed in one pass.
        // Ranking happens on the entity (before projection) because EF cannot translate an ordering
        // expression built from members of a projected row.
        var rows = await db
            .LibraryDocuments.AsNoTracking()
            .TagWithOperationCallSite("documents.library_document.search")
            .Where(document =>
                document.OrganizationId == organizationId
                && document.LibraryId == libraryId
                && (
                    document.SearchVector.Matches(EF.Functions.ToTsQuery("english", orQueryText))
                    || EF.Functions.TrigramsAreSimilar(document.TitleNormalized, fuzzyQuery)
                )
            )
            // Code-review execution never surfaces excluded (operational/informational) documents
            // or scoped skills. Skills are prompt content first, so review agents should not also
            // retrieve them as ordinary KB documents.
            // `excludeCodeReviewExcluded` is captured before the expression tree so the predicate
            // parameterizes as a plain bool and the query plan stays shared across both modes.
            .Where(document =>
                !excludeCodeReviewExcluded
                || (
                    !document.ExcludedFromCodeReviews
                    && document.AsScopedSkill == LibraryDocumentScopedSkill.None
                )
            )
            // Tiered combiner: a full-text hit (base 1.0) always outranks a fuzzy-only hit; the
            // full-text rank orders within that tier; the trigram score breaks ties and lifts
            // documents that match both signals. ts_rank_cd is 0 for non-full-text rows.
            .OrderByDescending(document =>
                (
                    document.SearchVector.Matches(EF.Functions.ToTsQuery("english", orQueryText))
                        ? 1.0
                        : 0.0
                )
                + document.SearchVector.RankCoverDensity(
                    SearchWeights,
                    EF.Functions.ToTsQuery("english", orQueryText),
                    SearchNormalization
                )
                + (
                    EF.Functions.TrigramsAreSimilar(document.TitleNormalized, fuzzyQuery)
                        ? EF.Functions.TrigramsSimilarity(document.TitleNormalized, fuzzyQuery)
                        : 0.0
                )
            )
            .ThenBy(document => document.Path)
            .Take(limit)
            .Select(document => new
            {
                Document = document,
                MatchesFullText = document.SearchVector.Matches(
                    EF.Functions.ToTsQuery("english", orQueryText)
                ),
                MatchesFuzzy = EF.Functions.TrigramsAreSimilar(
                    document.TitleNormalized,
                    fuzzyQuery
                ),
                FullTextScore = document.SearchVector.RankCoverDensity(
                    SearchWeights,
                    EF.Functions.ToTsQuery("english", orQueryText),
                    SearchNormalization
                ),
                FuzzyScore = EF.Functions.TrigramsSimilarity(document.TitleNormalized, fuzzyQuery),
            })
            .ToArrayAsync(ct);

        return Array.ConvertAll(
            rows,
            row => new LibraryDocumentMatch(
                row.Document,
                (row.MatchesFullText, row.MatchesFuzzy) switch
                {
                    (true, true) => DocumentMatchType.Both,
                    (true, false) => DocumentMatchType.FullText,
                    _ => DocumentMatchType.Fuzzy,
                },
                row.MatchesFullText ? row.FullTextScore : 0d,
                row.MatchesFuzzy ? row.FuzzyScore : 0d
            )
        );
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LibraryDocument>> ListDocumentsAsync(
        string organizationId,
        string libraryId,
        CancellationToken ct
    )
    {
        // Captured before the expression tree (same reasoning as SearchAsync): parameterizes as a
        // plain bool so both modes share one query plan.
        var excludeCodeReviewExcluded = searchScope.ForCodeReviewExecution;

        return await db
            .LibraryDocuments.AsNoTracking()
            .TagWithOperationCallSite("documents.library_document.list")
            .Where(document =>
                document.OrganizationId == organizationId && document.LibraryId == libraryId
            )
            // Code-review execution never lists excluded (operational/informational) documents
            // or scoped skills.
            // NOTE: This intentionally stays inline instead of a shared IQueryable helper for now;
            // keep this predicate in sync with SearchAsync above.
            .Where(document =>
                !excludeCodeReviewExcluded
                || (
                    !document.ExcludedFromCodeReviews
                    && document.AsScopedSkill == LibraryDocumentScopedSkill.None
                )
            )
            .OrderBy(document => document.Path)
            .ToArrayAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LibraryScopedSkillDocument>> ListScopedSkillDocumentsAsync(
        string organizationId,
        LibraryDocumentScopedSkill scopedSkill,
        CancellationToken ct
    ) =>
        await (
            from document in db
                .LibraryDocuments.AsNoTracking()
                .TagWithOperationCallSite("documents.library_document.list_scoped_skills")
            where document.OrganizationId == organizationId && document.AsScopedSkill == scopedSkill
            join library in db.Libraries.AsNoTracking()
                on new { document.OrganizationId, document.LibraryId } equals new
                {
                    library.OrganizationId,
                    LibraryId = library.Id,
                }
            orderby library.Name, document.Path
            select new LibraryScopedSkillDocument(
                document.OrganizationId,
                document.LibraryId,
                library.Name,
                document.Id,
                document.Path,
                document.Title,
                document.ManualSkillName,
                document.ParsedSkillName,
                document.ManualSkillDescription,
                document.ParsedSkillDescription,
                document.Metadata,
                null,
                document.UpdatedAt
            )
        ).ToArrayAsync(ct);

    /// <inheritdoc />
    public async Task<LibraryScopedSkillDocument?> ResolveScopedSkillDocumentAsync(
        string organizationId,
        string promptName,
        LibraryDocumentScopedSkill scopedSkill,
        CancellationToken ct
    )
    {
        var manualMatches = await ResolveScopedSkillCandidatesAsync(
            db.LibraryDocuments.AsNoTracking()
                .TagWithOperationCallSite("documents.library_document.resolve_scoped_skill.manual")
                .Where(document =>
                    document.OrganizationId == organizationId
                    && document.AsScopedSkill == scopedSkill
                    && document.ManualSkillName == promptName
                ),
            ct
        );
        if (manualMatches.Length > 0)
        {
            return manualMatches.Length == 1 ? manualMatches[0] : null;
        }

        var parsedMatches = await ResolveScopedSkillCandidatesAsync(
            db.LibraryDocuments.AsNoTracking()
                .TagWithOperationCallSite("documents.library_document.resolve_scoped_skill.parsed")
                .Where(document =>
                    document.OrganizationId == organizationId
                    && document.AsScopedSkill == scopedSkill
                    && document.ParsedSkillName == promptName
                ),
            ct
        );
        if (parsedMatches.Length > 0)
        {
            return parsedMatches.Length == 1 ? parsedMatches[0] : null;
        }

        var markdownPathSuffix = ReverseString($"/{promptName}.md");
        var mdxPathSuffix = ReverseString($"/{promptName}.mdx");
        var mdcPathSuffix = ReverseString($"/{promptName}.mdc");
        var pathMatches = await ResolveScopedSkillCandidatesAsync(
            db.LibraryDocuments.AsNoTracking()
                .TagWithOperationCallSite("documents.library_document.resolve_scoped_skill.path")
                .Where(document =>
                    document.OrganizationId == organizationId
                    && document.AsScopedSkill == scopedSkill
                    && (
                        document.PathReversed.StartsWith(markdownPathSuffix)
                        || document.PathReversed.StartsWith(mdxPathSuffix)
                        || document.PathReversed.StartsWith(mdcPathSuffix)
                    )
                ),
            ct
        );

        return pathMatches.Length == 1 ? pathMatches[0] : null;
    }

    /// <inheritdoc />
    public async Task<LibraryScopedSkillDocument?> GetScopedSkillDocumentAsync(
        string organizationId,
        string libraryId,
        string documentId,
        LibraryDocumentScopedSkill scopedSkill,
        CancellationToken ct
    ) =>
        await (
            from document in db
                .LibraryDocuments.AsNoTracking()
                .TagWithOperationCallSite("documents.library_document.get_scoped_skill")
            where
                document.OrganizationId == organizationId
                && document.LibraryId == libraryId
                && document.Id == documentId
                && document.AsScopedSkill == scopedSkill
            join library in db.Libraries.AsNoTracking()
                on new { document.OrganizationId, document.LibraryId } equals new
                {
                    library.OrganizationId,
                    LibraryId = library.Id,
                }
            select new LibraryScopedSkillDocument(
                document.OrganizationId,
                document.LibraryId,
                library.Name,
                document.Id,
                document.Path,
                document.Title,
                document.ManualSkillName,
                document.ParsedSkillName,
                document.ManualSkillDescription,
                document.ParsedSkillDescription,
                document.Metadata,
                document.Content,
                document.UpdatedAt
            )
        ).FirstOrDefaultAsync(ct);

    /// <summary>
    /// Loads up to two matching scoped-skill documents so callers can detect ambiguity cheaply.
    /// </summary>
    private async Task<LibraryScopedSkillDocument[]> ResolveScopedSkillCandidatesAsync(
        IQueryable<LibraryDocument> documents,
        CancellationToken ct
    ) =>
        await (
            from document in documents
            join library in db.Libraries.AsNoTracking()
                on new { document.OrganizationId, document.LibraryId } equals new
                {
                    library.OrganizationId,
                    LibraryId = library.Id,
                }
            orderby library.Name, document.Path
            select new LibraryScopedSkillDocument(
                document.OrganizationId,
                document.LibraryId,
                library.Name,
                document.Id,
                document.Path,
                document.Title,
                document.ManualSkillName,
                document.ParsedSkillName,
                document.ManualSkillDescription,
                document.ParsedSkillDescription,
                document.Metadata,
                null,
                document.UpdatedAt
            )
        )
            .Take(2)
            .ToArrayAsync(ct);

    /// <summary>
    /// Builds an OR-of-terms raw <c>tsquery</c> string by splitting on whitespace and joining with
    /// the <c>|</c> (OR) operator, for use with <c>EF.Functions.ToTsQuery(config, query)</c>
    /// (translates to Postgres's <c>to_tsquery</c>, server-side).
    /// </summary>
    /// <remarks>
    /// Deliberately simpler than <c>websearch_to_tsquery</c>: every space-separated term is an
    /// independent OR alternative, so adding more terms broadens results instead of narrowing them.
    /// This intentionally has no operator syntax — a caller-typed quote, <c>-</c>, or the word
    /// <c>or</c> is just another literal term to OR in, not a phrase/exclude/or operator (matches
    /// the v1 <c>PostgresStorageProvider.GetDocumentByKeywordsAsync</c> precedent exactly).
    /// <c>to_tsquery</c> does not sanitize caller input the way <c>websearch_to_tsquery</c> does, so
    /// a term containing <c>tsquery</c>-special characters (<c>&amp;</c>, <c>|</c>, <c>!</c>,
    /// parentheses, <c>&lt;-&gt;</c>) can throw or change meaning — the same trade-off v1 accepted
    /// for this simpler default. <see cref="NpgsqlTypes.NpgsqlTsQuery.Parse(string)"/> was
    /// considered instead (client-side parsing, parameterized as an ordinary value) but is marked
    /// obsolete by Npgsql itself as unreliable versus the server-side grammar, so the query text is
    /// built here and evaluated by Postgres's own <c>to_tsquery</c> instead.
    /// </remarks>
    private static string ToOrQueryText(string query)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return string.Join(" | ", terms);
    }
}
