using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <summary>
    /// Data-only migration (no schema change) remediating a live bug found via manual testing
    /// (2026-07-11): the <c>AddSnippetEmbeddingPayload</c> migration backfilled every pre-existing
    /// snippet row's new column to an empty string, and because <c>ReplaceForDocumentAsync</c> only
    /// set <c>EmbeddingPayload</c> on newly-inserted rows (not on rows matched as "unchanged" by
    /// <c>ContentHash</c>), every row that predated that migration kept an empty payload forever.
    /// The embedding pipeline then embedded those empty strings without error (the provider returns
    /// a normal-looking vector for empty input), so the vector search arm has been running on
    /// content-free embeddings for the entire pre-existing corpus. The application-code half of the
    /// fix (<c>ReplaceForDocumentAsync</c> now refreshes <c>EmbeddingPayload</c> on every reconcile,
    /// not just insert) closes the gap going forward; this migration remediates already-affected
    /// rows so the fix takes effect without a manual operator step in any environment this runs in
    /// (dev, staging, production).
    /// </summary>
    /// <inheritdoc />
    public partial class BackfillSnippetEmbeddingPayloadOnStaleDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Mark every currently-Indexed document Stale so the sweep's Pipeline A re-parses and
            // reconciles it on its next tick — with the code fix above, this repopulates
            // EmbeddingPayload for every affected row (ContentHash is unchanged, so reconciliation
            // matches the existing row rather than inserting a new one, but now also refreshes its
            // EmbeddingPayload instead of leaving the stale empty string in place).
            //
            // Failed is included too (code review follow-up, 2026-07-11): WriteAsync only stamps a
            // document Failed on a parse/compose error, and that branch never touches its snippet
            // rows — so a document that indexed successfully before this bug was introduced, then
            // later started failing to re-parse, would otherwise keep stale empty-payload snippets
            // forever (ClaimPendingIndexingAsync never auto-reclaims Failed rows). Forcing a retry
            // here is safe either way: if the document still fails to parse, WriteAsync just stamps
            // it Failed again, a no-op relative to today's state.
            migrationBuilder.Sql(
                """
                UPDATE zeeq.docs_library_documents
                SET processing_status = 'Stale', updated_at = now()
                WHERE processing_status IN ('Indexed', 'Failed');
                """
            );

            migrationBuilder.Sql(
                """
                UPDATE zeeq.docs_public_documents
                SET processing_status = 'Stale', updated_at = now()
                WHERE processing_status IN ('Indexed', 'Failed');
                """
            );

            // Clear the embedding + model stamp on every row whose payload is currently empty, so
            // Pipeline B's claim query (`embedding IS NULL OR embedding_model IS DISTINCT FROM
            // {model}`) picks them up again once Pipeline A's reconciliation above has repopulated
            // EmbeddingPayload. Pipeline A always fully drains before Pipeline B claims within the
            // same sweep tick (SnippetIndexingHostedService.RunAsync), so there is no race between
            // the reconcile and the re-embed — the very next tick both fixes the payload and
            // re-embeds it from the fixed payload.
            migrationBuilder.Sql(
                """
                UPDATE zeeq.docs_library_document_snippets
                SET embedding = NULL, embedding_model = NULL, embedding_started_at = NULL
                WHERE embedding_payload = '';
                """
            );

            migrationBuilder.Sql(
                """
                UPDATE zeeq.docs_public_document_snippets
                SET embedding = NULL, embedding_model = NULL, embedding_started_at = NULL
                WHERE embedding_payload = '';
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // NOTE: intentionally a no-op. This migration remediates bad data (empty embedding
            // payloads/vectors); there is no meaningful "undo" that wouldn't just reintroduce the
            // bug it fixes. Re-running the sweep is not destructive if this migration is ever
            // re-applied by mistake — Pipeline A's reconciliation is idempotent.
        }
    }
}
