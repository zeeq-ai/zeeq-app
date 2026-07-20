using Microsoft.EntityFrameworkCore;
using Zeeq.Core.Documents;

namespace Zeeq.Data.Postgres.Documents;

/// <summary>
/// Postgres-backed store for repository ingest run records.
/// </summary>
internal sealed class PostgresDocsIngestRunStore(PostgresDbContext db) : IDocsIngestRunStore
{
    /// <inheritdoc />
    public async Task<DocsIngestRun> CreateAsync(DocsIngestRun run, CancellationToken ct)
    {
        db.DocsIngestRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return run;
    }

    /// <inheritdoc />
    public Task<DocsIngestRun?> GetAsync(
        string id,
        DateTimeOffset createdAtUtc,
        CancellationToken ct
    ) =>
        db
            .DocsIngestRuns.TagWithOperationCallSite("docs_ingest_run.get")
            .SingleOrDefaultAsync(run => run.Id == id && run.CreatedAtUtc == createdAtUtc, ct);

    /// <inheritdoc />
    public async Task FinalizeAsync(
        string id,
        DateTimeOffset createdAtUtc,
        IngestRunFinalization finalization,
        CancellationToken ct
    )
    {
        var run = await db
            .DocsIngestRuns.TagWithOperationCallSite("docs_ingest_run.finalize_find")
            .SingleAsync(row => row.Id == id && row.CreatedAtUtc == createdAtUtc, ct);

        run.Status = finalization.Status;
        run.FilesTotal = finalization.FilesTotal;
        run.FilesAdded = finalization.FilesAdded;
        run.FilesUpdated = finalization.FilesUpdated;
        run.FilesMoved = finalization.FilesMoved;
        run.FilesSkipped = finalization.FilesSkipped;
        run.FilesDeleted = finalization.FilesDeleted;
        run.FilesFailed = finalization.FilesFailed;
        run.AuthFailure = finalization.AuthFailure;
        run.FailureMessage = finalization.FailureMessage;
        run.CompletedAtUtc = finalization.CompletedAtUtc;
        run.UpdatedAtUtc = finalization.CompletedAtUtc;

        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<bool> MarkStalledAsync(
        string id,
        DateTimeOffset createdAtUtc,
        DateTimeOffset completedAtUtc,
        string failureMessage,
        CancellationToken ct
    )
    {
        // NOTE: Repository sources/libraries have a "queued" lease state, but
        // DocsIngestRun rows are created only when dispatch begins, at which
        // point they start as Running. There is intentionally no Queued run
        // status to mark here.
        var updated = await db
            .DocsIngestRuns.TagWithOperationCallSite("docs_ingest_run.mark_stalled")
            .Where(run =>
                run.Id == id
                && run.CreatedAtUtc == createdAtUtc
                && run.Status == IngestRunStatus.Running
            )
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(run => run.Status, IngestRunStatus.Stalled)
                        .SetProperty(run => run.FailureMessage, failureMessage)
                        .SetProperty(run => run.CompletedAtUtc, completedAtUtc)
                        .SetProperty(run => run.UpdatedAtUtc, completedAtUtc),
                ct
            );

        return updated > 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DocsIngestRun>> ListByOrganizationAsync(
        string organizationId,
        int limit,
        CancellationToken ct
    ) =>
        await db
            .DocsIngestRuns.AsNoTracking()
            .TagWithOperationCallSite("docs_ingest_run.list_by_organization")
            .Where(run => run.OrganizationId == organizationId)
            .OrderByDescending(run => run.CreatedAtUtc)
            .Take(limit)
            .ToArrayAsync(ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<DocsIngestRun>> ListByLibraryAsync(
        string organizationId,
        string libraryId,
        DateTimeOffset? beforeCreatedAtUtc,
        string? beforeId,
        int limit,
        CancellationToken ct
    )
    {
        var query = db
            .DocsIngestRuns.AsNoTracking()
            .TagWithOperationCallSite("docs_ingest_run.list_by_library")
            .Where(run => run.OrganizationId == organizationId && run.LibraryId == libraryId);

        query = ApplyCursor(query, beforeCreatedAtUtc, beforeId);

        return await query
            .OrderByDescending(run => run.CreatedAtUtc)
            .ThenByDescending(run => run.Id)
            .Take(limit)
            .ToArrayAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DocsIngestRun>> ListByPublicSourceAsync(
        string publicSourceId,
        DateTimeOffset? beforeCreatedAtUtc,
        string? beforeId,
        int limit,
        CancellationToken ct
    )
    {
        var query = db
            .DocsIngestRuns.AsNoTracking()
            .TagWithOperationCallSite("docs_ingest_run.list_by_public_source")
            .Where(run => run.PublicSourceId == publicSourceId);

        query = ApplyCursor(query, beforeCreatedAtUtc, beforeId);

        return await query
            .OrderByDescending(run => run.CreatedAtUtc)
            .ThenByDescending(run => run.Id)
            .Take(limit)
            .ToArrayAsync(ct);
    }

    /// <summary>
    /// Keyset-pagination predicate shared by <see cref="ListByLibraryAsync"/>
    /// and <see cref="ListByPublicSourceAsync"/>: strictly-older-than the
    /// cursor row, matching the <c>created_at_utc DESC, id DESC</c> ordering
    /// both callers apply.
    /// </summary>
    /// <remarks>
    /// <paramref name="beforeCreatedAtUtc"/> and <paramref name="beforeId"/>
    /// must both be present or both be absent — they always come from a
    /// single decoded <c>IngestRunListCursor</c>, which encodes/decodes them
    /// as one opaque unit. A partial pair would silently degrade to an
    /// unpaged first page rather than failing, which is much harder to
    /// diagnose than a thrown exception, so this fails loud instead.
    /// </remarks>
    private static IQueryable<DocsIngestRun> ApplyCursor(
        IQueryable<DocsIngestRun> query,
        DateTimeOffset? beforeCreatedAtUtc,
        string? beforeId
    )
    {
        if (beforeCreatedAtUtc.HasValue != (beforeId is not null))
        {
            throw new ArgumentException(
                $"{nameof(beforeCreatedAtUtc)} and {nameof(beforeId)} must be supplied together."
            );
        }

        return beforeCreatedAtUtc is { } cursorCreatedAtUtc && beforeId is not null
            ? query.Where(run =>
                run.CreatedAtUtc < cursorCreatedAtUtc
                || (run.CreatedAtUtc == cursorCreatedAtUtc && string.Compare(run.Id, beforeId) < 0)
            )
            : query;
    }
}
