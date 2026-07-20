namespace Zeeq.Core.Documents;

/// <summary>
/// Domain-slice interface for library document storage.
/// </summary>
/// <remarks>
/// API handlers and MCP tools depend on this abstraction instead of depending on
/// the Postgres <c>DbContext</c> directly.
/// </remarks>
public interface ILibraryDocumentStore : IIndexableDocumentStore<LibraryDocument>
{
    /// <summary>
    /// Gets a library by organization and name.
    /// </summary>
    Task<Library?> GetLibraryAsync(string organizationId, string name, CancellationToken ct);

    /// <summary>
    /// Gets a library by organization and id.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="GetLibraryAsync"/> because the ingest pipeline
    /// only ever carries a library id (from <c>PrivateRepositorySyncRequested</c>,
    /// minted by the publisher) — it never has the human-facing name a route
    /// parameter would supply.
    /// </remarks>
    Task<Library?> GetLibraryByIdAsync(
        string organizationId,
        string libraryId,
        CancellationToken ct
    );

    /// <summary>
    /// Lists libraries in an organization.
    /// </summary>
    Task<IReadOnlyList<Library>> ListLibrariesAsync(string organizationId, CancellationToken ct);

    /// <summary>
    /// Lists every library across all organizations subscribed to a public source
    /// — i.e. libraries whose <see cref="Library.PublicSourceId"/> matches.
    /// </summary>
    /// <remarks>
    /// Deliberately not scoped to one organization: a public-source ingest run
    /// serves every subscribing org from the same shared table (spec §2.4), so
    /// the effective filter for one run is the union of every subscriber's
    /// filter, not just one org's. No further org-membership/read-access check
    /// is layered on top here — subscribing a library to a public source (which
    /// requires org-scoped auth at creation time) is itself the access grant;
    /// there is no separate "orgs with read access" concept beyond that.
    /// </remarks>
    Task<IReadOnlyList<Library>> ListLibrariesByPublicSourceIdAsync(
        string publicSourceId,
        CancellationToken ct
    );

    /// <summary>
    /// Atomically claims due private-source libraries across every organization
    /// and marks them <c>queued</c>, mirroring
    /// <see cref="IDocsPublicSourceStore.ClaimDueForSyncAsync"/> for public
    /// sources.
    /// </summary>
    /// <remarks>
    /// Scoped to libraries with a non-null <c>SourceKind</c> — local
    /// (hand-authored) and public-source libraries have no sync lifecycle of
    /// their own and are never claimed here.
    /// </remarks>
    Task<IReadOnlyList<Library>> ClaimDueForSyncAsync(int limit, CancellationToken ct);

    /// <summary>
    /// Creates a library.
    /// </summary>
    Task<Library> CreateLibraryAsync(Library library, CancellationToken ct);

    /// <summary>
    /// Updates an existing library.
    /// </summary>
    Task<Library> UpdateLibraryAsync(Library library, CancellationToken ct);

    /// <summary>
    /// Deletes a library and its documents.
    /// </summary>
    Task DeleteLibraryAsync(string organizationId, string libraryId, CancellationToken ct);

    /// <summary>
    /// Updates a private-source library's sync lifecycle fields only —
    /// <c>sync_status</c>, <c>next_sync_at</c>, and <c>manual_trigger_history</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately narrower than <see cref="UpdateLibraryAsync"/>, which only
    /// copies <c>Name</c>/<c>Description</c> and would silently no-op these
    /// fields if reused here. Manual-trigger rate limiting and ingest
    /// idempotency need to mutate sync state without touching (or requiring
    /// the caller to know) the library's display fields.
    /// <para>
    /// <c>sourceSyncedAt</c> is required (not optional) so every call site
    /// states its intent explicitly — pass the library's existing
    /// <see cref="Library.SourceSyncedAt"/> unchanged for callers that aren't
    /// finishing a sync (e.g. the manual-trigger endpoint), or the new
    /// timestamp for callers that are (the ingest consumer, on success).
    /// </para>
    /// </remarks>
    Task<Library> UpdateSyncStateAsync(
        string organizationId,
        string libraryId,
        string? syncStatus,
        DateTimeOffset? nextSyncAt,
        DateTimeOffset[] manualTriggerHistory,
        DateTimeOffset? sourceSyncedAt,
        CancellationToken ct
    );

    /// <summary>
    /// Updates a private-source library's sync lifecycle fields plus active-run lease fields.
    /// </summary>
    Task<Library> UpdateSyncLeaseAsync(
        string organizationId,
        string libraryId,
        string? syncStatus,
        DateTimeOffset? nextSyncAt,
        DateTimeOffset[] manualTriggerHistory,
        DateTimeOffset? sourceSyncedAt,
        string? activeSyncRunId,
        DateTimeOffset? activeSyncRunCreatedAtUtc,
        DateTimeOffset? syncQueuedAtUtc,
        DateTimeOffset? syncStartedAtUtc,
        CancellationToken ct
    )
    {
        return UpdateSyncStateAsync(
            organizationId,
            libraryId,
            syncStatus,
            nextSyncAt,
            manualTriggerHistory,
            sourceSyncedAt,
            ct
        );
    }

    /// <summary>
    /// Updates sync fields only if the library is still owned by the expected active run.
    /// </summary>
    Task<bool> TryUpdateCurrentSyncLeaseAsync(
        string organizationId,
        string libraryId,
        string expectedRunId,
        DateTimeOffset expectedRunCreatedAtUtc,
        string? syncStatus,
        DateTimeOffset? nextSyncAt,
        DateTimeOffset[] manualTriggerHistory,
        DateTimeOffset? sourceSyncedAt,
        string? activeSyncRunId,
        DateTimeOffset? activeSyncRunCreatedAtUtc,
        DateTimeOffset? syncQueuedAtUtc,
        DateTimeOffset? syncStartedAtUtc,
        CancellationToken ct
    )
    {
        throw new NotSupportedException();
    }

    /// <summary>
    /// Clears a private-source library's active sync state and makes it eligible to run now.
    /// </summary>
    Task<LibrarySyncStateReset?> ResetLibrarySyncStateAsync(
        string organizationId,
        string libraryId,
        DateTimeOffset now,
        CancellationToken ct
    )
    {
        throw new NotSupportedException();
    }

    /// <summary>
    /// Clears stale queued/running private-source library syncs.
    /// </summary>
    Task<IReadOnlyList<StalledSyncReset>> ResetStalledSyncsAsync(
        DateTimeOffset now,
        TimeSpan queuedStaleAfter,
        TimeSpan runningStaleAfter,
        int limit,
        CancellationToken ct
    )
    {
        throw new NotSupportedException();
    }

    /// <summary>
    /// Inserts or updates a document by normalized path.
    /// </summary>
    Task<LibraryDocument> UpsertDocumentAsync(LibraryDocument document, CancellationToken ct);

    /// <summary>
    /// Inserts, updates, or move-detects a document within one library, using
    /// the same four-way resolution as
    /// <see cref="IDocsPublicDocumentStore.UpsertAsync"/> — scoped to
    /// <c>(organization_id, library_id)</c> instead of <c>public_source_id</c>.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="UpsertDocumentAsync"/> (a plain path-keyed
    /// upsert with no hash comparison or move detection, used by the
    /// hand-authored document API) because move detection and the
    /// <c>DeleteUnstampedAsync</c> sweep are only meaningful for
    /// machine-synced content that carries a <c>sync_run_id</c> — mixing the
    /// two write paths on one method would make hand-authored edits
    /// accidentally participate in move-detection heuristics they were never
    /// designed for.
    /// </remarks>
    Task<LibraryDocumentUpsertResult> UpsertSyncedDocumentAsync(
        LibraryDocument document,
        CancellationToken ct
    );

    /// <summary>
    /// Deletes documents in <paramref name="libraryId"/> whose <c>sync_run_id</c>
    /// does not match <paramref name="currentSyncRunId"/> — the deletion sweep
    /// run only after a clean (no per-file failures) pass. Returns the number
    /// of rows deleted.
    /// </summary>
    /// <remarks>
    /// Only meaningful for private-source libraries; a local (hand-authored)
    /// library should never have this called against it, since every document
    /// in it has a null <c>sync_run_id</c> and would be swept.
    /// </remarks>
    Task<int> DeleteUnstampedAsync(
        string organizationId,
        string libraryId,
        string currentSyncRunId,
        CancellationToken ct
    );

    /// <summary>
    /// Deletes a document by path.
    /// </summary>
    Task DeleteDocumentAsync(
        string organizationId,
        string libraryId,
        string path,
        CancellationToken ct
    );

    /// <summary>
    /// Gets the first matching document by exact path, suffix path, or file name.
    /// </summary>
    Task<LibraryDocument?> GetByPathAsync(
        string organizationId,
        string libraryId,
        string input,
        CancellationToken ct
    );

    /// <summary>
    /// Gets a document by stable id within one library.
    /// </summary>
    Task<LibraryDocument?> GetByIdAsync(
        string organizationId,
        string libraryId,
        string documentId,
        CancellationToken ct
    ) =>
        Task.FromException<LibraryDocument?>(
            new NotSupportedException(
                $"{nameof(GetByIdAsync)} must be implemented by stores that support identity-based document mutations."
            )
        );

    /// <summary>
    /// Runs combined retrieval: ranked websearch full-text plus fuzzy trigram title matching.
    /// </summary>
    /// <remarks>
    /// A single query unions both signals. Full-text hits always outrank fuzzy-only hits, and a
    /// document matching both signals ranks highest. Each result carries its match type and
    /// per-signal scores.
    /// </remarks>
    Task<IReadOnlyList<LibraryDocumentMatch>> SearchAsync(
        string organizationId,
        string libraryId,
        string query,
        int limit,
        CancellationToken ct
    );

    /// <summary>
    /// Lists documents in a library.
    /// </summary>
    Task<IReadOnlyList<LibraryDocument>> ListDocumentsAsync(
        string organizationId,
        string libraryId,
        CancellationToken ct
    );

    /// <summary>
    /// Moves a document to a new normalized path, recording the old path in
    /// <see cref="LibraryDocument.PreviousPaths"/> (D-3, D-4).
    /// </summary>
    /// <remarks>
    /// Returns the moved entity after save, or <c>null</c> when the source path does not resolve.
    /// Throws <see cref="DuplicateDocumentPathException"/> when the target path collides with
    /// an existing live path or previous-path alias.
    /// </remarks>
    /// <param name="organizationId">Owning organization.</param>
    /// <param name="libraryId">Library containing the document.</param>
    /// <param name="fromPath">Current document path (resolved via the same tiered walk as <see cref="GetByPathAsync"/>).</param>
    /// <param name="toPath">Target normalized path. Must not collide with any existing live path or alias.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The moved document, or <c>null</c> when <paramref name="fromPath"/> does not resolve.</returns>
    Task<LibraryDocument?> MoveDocumentAsync(
        string organizationId,
        string libraryId,
        string fromPath,
        string toPath,
        CancellationToken ct
    );

    /// <summary>
    /// Sets or clears <see cref="LibraryDocument.ExcludedFromCodeReviews"/> on a resolved document.
    /// </summary>
    /// <remarks>
    /// Narrow field update — mirrors <see cref="UpdateSyncStateAsync"/>'s pattern of mutating
    /// lifecycle state without requiring the caller to round-trip the whole document body.
    /// The store does not gate on <see cref="LibraryDocument.SyncRunId"/>; the API handler
    /// rejects synced documents before calling this (v1 scopes exclusion to hand-authored
    /// documents, whose lifecycle a sync run does not own).
    /// </remarks>
    /// <param name="organizationId">Owning organization.</param>
    /// <param name="libraryId">Library containing the document.</param>
    /// <param name="documentId">Stable document identifier.</param>
    /// <param name="excluded">True to hide the document from code-review list/search results.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated document, or <c>null</c> when <paramref name="documentId"/> does not resolve.</returns>
    Task<LibraryDocument?> SetCodeReviewExclusionAsync(
        string organizationId,
        string libraryId,
        string documentId,
        bool excluded,
        CancellationToken ct
    );
}

/// <summary>
/// Persisted document plus the upsert outcome that produced it — the
/// library-scoped counterpart to <see cref="DocsPublicDocumentUpsertResult"/>.
/// </summary>
/// <param name="Document">The persisted row.</param>
/// <param name="Kind">Which of the four upsert branches applied.</param>
public sealed record LibraryDocumentUpsertResult(LibraryDocument Document, DocumentUpsertKind Kind);
