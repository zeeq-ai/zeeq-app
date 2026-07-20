using Zeeq.Core.Documents;
using Zeeq.Core.Documents.Snippets;
using Zeeq.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Zeeq.Data.Postgres.Configurations;

internal sealed class LibraryConfiguration : IEntityTypeConfiguration<Library>
{
    public void Configure(EntityTypeBuilder<Library> entity)
    {
        entity.ToTable("docs_libraries");
        entity.HasKey(library => new { library.OrganizationId, library.Id });

        entity.Property(library => library.Id).HasMaxLength(128);
        entity.Property(library => library.OrganizationId).IsRequired().HasMaxLength(128);
        entity.Property(library => library.TeamId).HasMaxLength(128);
        entity.Property(library => library.Name).IsRequired().HasMaxLength(200);
        entity.Property(library => library.Description).HasMaxLength(2000);

        // Public source linkage — mutually exclusive with private source columns.
        entity.Property(library => library.PublicSourceId).HasMaxLength(128);
        entity.Property(library => library.IncludeFilters).HasColumnType("text[]");
        entity.Property(library => library.ExcludeFilters).HasColumnType("text[]");

        // Private source metadata — mutually exclusive with PublicSourceId.
        entity.Property(library => library.SourceKind).HasMaxLength(64);
        entity.Property(library => library.SourceRepoUrl).HasMaxLength(2048);
        entity.Property(library => library.SourceDefaultIncludeFilters).HasColumnType("text[]");
        entity.Property(library => library.SourceDefaultExcludeFilters).HasColumnType("text[]");

        // Sync lifecycle.
        entity.Property(library => library.SyncStatus).HasMaxLength(32);
        entity.Property(library => library.ActiveSyncRunId).HasMaxLength(128);
        entity.Property(library => library.ManualTriggerHistory).HasColumnType("timestamptz[]");

        entity.Property(library => library.CreatedAt).IsRequired();
        entity.Property(library => library.UpdatedAt).IsRequired();

        entity.HasIndex(library => new { library.OrganizationId, library.Name }).IsUnique();
        entity.HasIndex(library => new { library.OrganizationId, library.TeamId });

        // Fast path for the scheduler's atomic claim query.
        entity.HasIndex(library => new { library.SyncStatus, library.NextSyncAt });
        // Fast path for stalled sync cleanup.
        // NOTE: Unlike request-path library indexes, this worker sweep is intentionally global
        // and has no organization_id predicate; leading with SyncStatus matches the recovery
        // query's WHERE clause instead of the normal org-prefixed access pattern.
        entity.HasIndex(library => new { library.SyncStatus, library.SyncQueuedAtUtc });
        entity.HasIndex(library => new { library.SyncStatus, library.SyncStartedAtUtc });
        // Resolve libraries that subscribe to a given public source.
        entity.HasIndex(library => library.PublicSourceId);

        entity
            .HasOne<Organization>()
            .WithMany()
            .HasForeignKey(library => library.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
        entity
            .HasOne<Team>()
            .WithMany()
            .HasPrincipalKey(team => new { team.OrganizationId, team.Id })
            .HasForeignKey(library => new { library.OrganizationId, library.TeamId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class LibraryDocumentConfiguration : IEntityTypeConfiguration<LibraryDocument>
{
    public void Configure(EntityTypeBuilder<LibraryDocument> entity)
    {
        entity.ToTable("docs_library_documents");
        entity.HasKey(document => new
        {
            document.OrganizationId,
            document.LibraryId,
            document.Id,
        });

        entity.Property(document => document.Id).HasMaxLength(128);
        entity.Property(document => document.OrganizationId).IsRequired().HasMaxLength(128);
        entity.Property(document => document.TeamId).HasMaxLength(128);
        entity.Property(document => document.LibraryId).IsRequired().HasMaxLength(128);
        entity.Property(document => document.Path).IsRequired().HasMaxLength(2048);
        entity
            .Property(document => document.PathReversed)
            .HasComputedColumnSql("reverse(path)", stored: true);
        entity.Property(document => document.Title).IsRequired().HasMaxLength(512);
        entity.Property(document => document.TitleNormalized).IsRequired().HasMaxLength(512);
        entity.Property(document => document.Keywords).HasColumnType("text[]");
        entity.Property(document => document.Headings).HasColumnType("text[]");

        // PreviousPaths is a text[] with a GIN index for fast membership lookup
        // when resolving old paths after a rename (D-4). The index is created as
        // raw SQL in the migration to include the distribution keys.
        entity.Property(document => document.PreviousPaths).HasColumnType("text[]");
        entity
            .HasIndex(document => document.PreviousPaths)
            .HasMethod("GIN")
            .HasDatabaseName("ix_docs_library_documents_previous_paths");
        entity.Property(document => document.Content).IsRequired();
        entity
            .Property(document => document.SearchVector)
            .HasColumnType("tsvector")
            .HasComputedColumnSql(
                """
                setweight(to_tsvector('english', coalesce(title, '')), 'A') ||
                setweight(to_tsvector('simple',  coalesce(zeeq.immutable_array_to_string(keywords, ' '), '')), 'B') ||
                setweight(to_tsvector('english', coalesce(zeeq.immutable_array_to_string(headings, ' '), '')), 'C') ||
                setweight(to_tsvector('english', coalesce(content, '')), 'D')
                """,
                stored: true
            );
        entity
            .Property(document => document.ProcessingStatus)
            .IsRequired()
            .HasMaxLength(32)
            .HasConversion<string>();
        entity.Property(document => document.TokenCount).IsRequired();
        entity.Property(document => document.ContentHash).HasMaxLength(64);
        entity.Property(document => document.SyncRunId).HasMaxLength(128);

        // Review-exclusion flag: NOT NULL DEFAULT FALSE so existing rows stay visible to code
        // reviews after the migration; no index — the filter always composes with the
        // (organization_id, library_id) index and the flag is low-selectivity.
        entity
            .Property(document => document.ExcludedFromCodeReviews)
            .IsRequired()
            .HasDefaultValue(false);

        entity.Property(document => document.CreatedAt).IsRequired();
        entity.Property(document => document.UpdatedAt).IsRequired();

        entity.OwnsOne(
            document => document.SourceOrigin,
            sourceOrigin =>
            {
                sourceOrigin.ToJson("source_origin");
                sourceOrigin.Property(origin => origin.Kind).HasMaxLength(64);
                sourceOrigin.Property(origin => origin.RepoRef).HasMaxLength(512);
            }
        );

        entity
            .HasIndex(document => new
            {
                document.OrganizationId,
                document.LibraryId,
                document.Path,
            })
            .IsUnique()
            .HasDatabaseName("ix_docs_library_documents_path");

        // Index for the deletion sweep: efficiently find documents not stamped by
        // the current run within a given library.
        entity.HasIndex(document => new
        {
            document.OrganizationId,
            document.LibraryId,
            document.SyncRunId,
        });

        // Supports the move-detection hash lookup in UpsertSyncedDocumentAsync
        // (per-file on every private ingest run) — mirrors
        // ix_docs_public_documents_public_source_id_content_hash, which this
        // table was missing.
        entity
            .HasIndex(document => new
            {
                document.OrganizationId,
                document.LibraryId,
                document.ContentHash,
            })
            .HasDatabaseName("ix_docs_library_documents_organization_id_library_id_content_h");

        // The search_vector GIN, the path_reversed prefix index, the title trigram GIN, and the
        // processing_status partial index are created as raw SQL in the migration. They lead with
        // the (organization_id, library_id) distribution keys via btree_gin / text_pattern_ops,
        // which the fluent index API cannot express, so they are intentionally not modeled here.

        entity
            .HasOne<Library>()
            .WithMany()
            .HasForeignKey(document => new { document.OrganizationId, document.LibraryId })
            .OnDelete(DeleteBehavior.Cascade);
        entity
            .HasOne<Team>()
            .WithMany()
            .HasPrincipalKey(team => new { team.OrganizationId, team.Id })
            .HasForeignKey(document => new { document.OrganizationId, document.TeamId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>
/// EF mapping for public repository sources registered for global document ingest.
/// </summary>
internal sealed class DocsPublicSourceConfiguration : IEntityTypeConfiguration<DocsPublicSource>
{
    public void Configure(EntityTypeBuilder<DocsPublicSource> entity)
    {
        entity.ToTable("docs_public_sources");
        entity.HasKey(source => source.Id);

        entity.Property(source => source.Id).HasMaxLength(128);
        entity
            .Property(source => source.Kind)
            .IsRequired()
            .HasMaxLength(32)
            .HasConversion<string>();
        entity.Property(source => source.RepoUrl).IsRequired().HasMaxLength(2048);
        entity.Property(source => source.Name).IsRequired().HasMaxLength(200);
        entity.Property(source => source.DefaultIncludeFilters).HasColumnType("text[]");
        entity.Property(source => source.DefaultExcludeFilters).HasColumnType("text[]");
        entity.Property(source => source.SyncStatus).IsRequired().HasMaxLength(32);
        entity.Property(source => source.ActiveSyncRunId).HasMaxLength(128);
        entity.Property(source => source.Status).IsRequired().HasMaxLength(32);
        entity.Property(source => source.ManualTriggerHistory).HasColumnType("timestamptz[]");
        entity.Property(source => source.CreatedAt).IsRequired();
        entity.Property(source => source.UpdatedAt).IsRequired();

        // Ensure one row per public repo URL.
        entity.HasIndex(source => source.RepoUrl).IsUnique();
        // Scheduler atomic claim query.
        entity.HasIndex(source => new
        {
            source.Status,
            source.SyncStatus,
            source.NextSyncAt,
        });
        // Stalled sync cleanup.
        entity.HasIndex(source => new { source.SyncStatus, source.SyncQueuedAtUtc });
        entity.HasIndex(source => new { source.SyncStatus, source.SyncStartedAtUtc });
    }
}

/// <summary>
/// EF mapping for documents ingested from public repositories, shared globally.
/// </summary>
/// <remarks>
/// There is no organization column — one row serves all subscribing orgs. Per-org
/// visibility is enforced at query time via library filters. Move detection uses
/// the <c>(public_source_id, content_hash)</c> index. Copy-on-write indexing (GIN,
/// text_pattern_ops, trigram) that cannot be expressed in the fluent API is created
/// as raw SQL in the migration, mirroring <c>docs_library_documents</c>.
/// </remarks>
internal sealed class DocsPublicDocumentConfiguration : IEntityTypeConfiguration<DocsPublicDocument>
{
    public void Configure(EntityTypeBuilder<DocsPublicDocument> entity)
    {
        entity.ToTable("docs_public_documents");
        entity.HasKey(document => document.Id);

        entity.Property(document => document.Id).HasMaxLength(128);
        entity.Property(document => document.PublicSourceId).IsRequired().HasMaxLength(128);
        entity.Property(document => document.Path).IsRequired().HasMaxLength(2048);
        entity
            .Property(document => document.PathReversed)
            .HasComputedColumnSql("reverse(path)", stored: true);
        entity.Property(document => document.Title).IsRequired().HasMaxLength(512);
        entity.Property(document => document.TitleNormalized).IsRequired().HasMaxLength(512);
        entity.Property(document => document.Keywords).HasColumnType("text[]");
        entity.Property(document => document.Headings).HasColumnType("text[]");
        entity.Property(document => document.PreviousPaths).HasColumnType("text[]");
        entity.Property(document => document.Content).IsRequired();
        entity
            .Property(document => document.SearchVector)
            .HasColumnType("tsvector")
            .HasComputedColumnSql(
                """
                setweight(to_tsvector('english', coalesce(title, '')), 'A') ||
                setweight(to_tsvector('simple',  coalesce(zeeq.immutable_array_to_string(keywords, ' '), '')), 'B') ||
                setweight(to_tsvector('english', coalesce(zeeq.immutable_array_to_string(headings, ' '), '')), 'C') ||
                setweight(to_tsvector('english', coalesce(content, '')), 'D')
                """,
                stored: true
            );
        entity.Property(document => document.ContentHash).IsRequired().HasMaxLength(64);
        entity.Property(document => document.TokenCount).IsRequired();
        entity
            .Property(document => document.ProcessingStatus)
            .IsRequired()
            .HasMaxLength(32)
            .HasConversion<string>();
        entity.Property(document => document.SyncRunId).HasMaxLength(128);
        entity.Property(document => document.CreatedAt).IsRequired();
        entity.Property(document => document.UpdatedAt).IsRequired();

        // Unique path per source; core dedup key.
        entity.HasIndex(document => new { document.PublicSourceId, document.Path }).IsUnique();
        // Move detection: content hash lookup scoped to the source.
        entity.HasIndex(document => new { document.PublicSourceId, document.ContentHash });
        // Sweep: find unstamped documents for the current run.
        entity.HasIndex(document => new { document.PublicSourceId, document.SyncRunId });

        // The search_vector GIN, path_reversed prefix, title trigram GIN, and previous_paths
        // GIN indexes are created as raw SQL in the migration.

        entity
            .HasOne<DocsPublicSource>()
            .WithMany()
            .HasForeignKey(document => document.PublicSourceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>
/// EF mapping for partitioned ingest run records.
/// </summary>
/// <remarks>
/// Each row is one repository import execution attempt. The table is range-partitioned by
/// <c>CreatedAtUtc</c> with 14-day intervals managed by pg_partman. The composite primary
/// key includes the partition column as required by PostgreSQL. Indexes and partitioning
/// setup are created as raw SQL in the migration.
/// </remarks>
internal sealed class DocsIngestRunConfiguration : IEntityTypeConfiguration<DocsIngestRun>
{
    public void Configure(EntityTypeBuilder<DocsIngestRun> entity)
    {
        entity.ToTable("docs_ingest_runs");
        entity.HasKey(run => new { run.Id, run.CreatedAtUtc });

        entity.Property(run => run.Id).HasMaxLength(128);
        entity
            .Property(run => run.SourceKind)
            .IsRequired()
            .HasMaxLength(32)
            .HasConversion<string>();
        entity.Property(run => run.RepoUrl).IsRequired().HasMaxLength(2048);
        entity.Property(run => run.PublicSourceId).HasMaxLength(128);
        entity.Property(run => run.OrganizationId).HasMaxLength(128);
        entity.Property(run => run.LibraryId).HasMaxLength(128);
        entity.Property(run => run.Trigger).IsRequired().HasMaxLength(16).HasConversion<string>();
        entity.Property(run => run.Status).IsRequired().HasMaxLength(32).HasConversion<string>();
        entity.Property(run => run.RootTraceId).HasMaxLength(64);
        entity.Property(run => run.FailureMessage).HasMaxLength(4096);
        entity.Property(run => run.CreatedAtUtc).IsRequired();
        entity.Property(run => run.UpdatedAtUtc).IsRequired();

        // Find runs for a given public source, ordered by recency.
        entity.HasIndex(run => new { run.PublicSourceId, run.CreatedAtUtc });
        // Find runs for a private (org, library) combination.
        entity.HasIndex(run => new
        {
            run.OrganizationId,
            run.LibraryId,
            run.CreatedAtUtc,
        });
        // Diagnostic: find runs with auth failures.
        entity.HasIndex(run => new
        {
            run.SourceKind,
            run.AuthFailure,
            run.CreatedAtUtc,
        });
    }
}

/// <summary>
/// EF mapping for private library document snippets (prose sections and code samples).
/// </summary>
/// <remarks>
/// Snippets carry the org/library distribution keys and cascade-delete with their owning
/// <see cref="LibraryDocument"/> — the store's <c>ExecuteDeleteAsync</c> reconciliation sweep and
/// document deletes both rely on the DB-level cascade. The <c>embedding</c> (<c>halfvec(768)</c>),
/// the HNSW/GIN/lease indexes, and <c>STORAGE EXTERNAL</c> on the vector column are declared as raw
/// SQL in the migration because the fluent API cannot express them; the <c>search_vector</c> stored
/// computed column mirrors the document tables' weighting scheme adapted for snippet fields.
/// </remarks>
internal sealed class LibraryDocumentSnippetConfiguration
    : IEntityTypeConfiguration<LibraryDocumentSnippet>
{
    public void Configure(EntityTypeBuilder<LibraryDocumentSnippet> entity)
    {
        entity.ToTable("docs_library_document_snippets");
        entity.HasKey(snippet => new
        {
            snippet.OrganizationId,
            snippet.LibraryId,
            snippet.Id,
        });

        entity.Property(snippet => snippet.Id).HasMaxLength(128);
        entity.Property(snippet => snippet.OrganizationId).IsRequired().HasMaxLength(128);
        entity.Property(snippet => snippet.TeamId).HasMaxLength(128);
        entity.Property(snippet => snippet.LibraryId).IsRequired().HasMaxLength(128);
        entity.Property(snippet => snippet.DocumentId).IsRequired().HasMaxLength(128);
        entity
            .Property(snippet => snippet.Kind)
            .IsRequired()
            .HasMaxLength(16)
            .HasConversion<string>();
        entity.Property(snippet => snippet.Header).IsRequired().HasMaxLength(1024);
        entity.Property(snippet => snippet.HeadingPath).IsRequired().HasMaxLength(2048);
        entity.Property(snippet => snippet.Language).HasMaxLength(64);
        entity.Property(snippet => snippet.Tag).HasMaxLength(256);
        entity.Property(snippet => snippet.PrecedingText);
        entity.Property(snippet => snippet.Content).IsRequired();
        entity.Property(snippet => snippet.EmbeddingPayload).IsRequired();
        entity.Property(snippet => snippet.Identifiers).HasColumnType("text[]");
        entity.Property(snippet => snippet.ContentHash).IsRequired().HasMaxLength(64);
        entity.Property(snippet => snippet.Ordinal).IsRequired();
        entity.Property(snippet => snippet.TokenCount).IsRequired();

        // Embedding is halfvec(768); the column type, HNSW index, and STORAGE EXTERNAL are
        // finalized in raw SQL in the migration. Declaring the type here keeps the model snapshot
        // consistent with the migration DDL.
        entity.Property(snippet => snippet.Embedding).HasColumnType("halfvec(768)");
        entity.Property(snippet => snippet.EmbeddingModel).HasMaxLength(128);
        entity.Property(snippet => snippet.EmbeddingStartedAt);

        entity
            .Property(snippet => snippet.SearchVector)
            .HasColumnType("tsvector")
            .HasComputedColumnSql(
                """
                setweight(to_tsvector('english', coalesce(heading_path, '')), 'A') ||
                setweight(to_tsvector('simple',  coalesce(zeeq.immutable_array_to_string(identifiers, ' '), '')), 'B') ||
                setweight(to_tsvector('simple',  coalesce(language, '') || ' ' || coalesce(tag, '')), 'B') ||
                setweight(to_tsvector('english', coalesce(content, '')), 'D')
                """,
                stored: true
            );

        entity.Property(snippet => snippet.CreatedAt).IsRequired();
        entity.Property(snippet => snippet.UpdatedAt).IsRequired();

        // Reconciliation lookup during ReplaceForDocumentAsync — see the matching raw-SQL index in
        // the migration (leads with the distribution keys). Modeled here for the reconcile query
        // predicate; the migration owns the physical index so the ordering matches the KB rule.
        // NOTE: No unique constraint on (document_id, kind, content_hash, ordinal) by design —
        // reconciliation runs in a single transaction per document, and the sweep claims each
        // document exclusively via FOR UPDATE SKIP LOCKED (Slice 2/3), so concurrent writers for
        // the same document cannot race. Ordinal already disambiguates identical payloads within a
        // document. A unique index would add write cost for a collision that the claim model
        // prevents; revisit only if the single-writer-per-document invariant ever changes.
        entity
            .HasIndex(snippet => new
            {
                snippet.OrganizationId,
                snippet.LibraryId,
                snippet.DocumentId,
            })
            .HasDatabaseName("ix_docs_library_document_snippets_document");

        // FK to the owning document on the full (org, library, id) key, ON DELETE CASCADE so
        // deleting a document removes its snippets at the DB level.
        entity
            .HasOne<LibraryDocument>()
            .WithMany()
            .HasForeignKey(snippet => new
            {
                snippet.OrganizationId,
                snippet.LibraryId,
                snippet.DocumentId,
            })
            .HasPrincipalKey(document => new
            {
                document.OrganizationId,
                document.LibraryId,
                document.Id,
            })
            .OnDelete(DeleteBehavior.Cascade);

        // Team FK mirrors LibraryDocumentConfiguration (Restrict).
        entity
            .HasOne<Team>()
            .WithMany()
            .HasPrincipalKey(team => new { team.OrganizationId, team.Id })
            .HasForeignKey(snippet => new { snippet.OrganizationId, snippet.TeamId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>
/// EF mapping for global public document snippets, shared across subscribing organizations.
/// </summary>
/// <remarks>
/// The public sibling of <see cref="LibraryDocumentSnippetConfiguration"/>: no org/team/library
/// columns, scoped by <c>public_source_id</c> instead, and cascade-deleted with the owning
/// <see cref="DocsPublicDocument"/>. HNSW/GIN/lease indexes and <c>STORAGE EXTERNAL</c> are declared
/// in raw SQL in the migration, mirroring the private table without the org/library prefix.
/// </remarks>
internal sealed class PublicDocumentSnippetConfiguration
    : IEntityTypeConfiguration<PublicDocumentSnippet>
{
    public void Configure(EntityTypeBuilder<PublicDocumentSnippet> entity)
    {
        entity.ToTable("docs_public_document_snippets");
        entity.HasKey(snippet => snippet.Id);

        entity.Property(snippet => snippet.Id).HasMaxLength(128);
        entity.Property(snippet => snippet.PublicSourceId).IsRequired().HasMaxLength(128);
        entity.Property(snippet => snippet.DocumentId).IsRequired().HasMaxLength(128);
        entity
            .Property(snippet => snippet.Kind)
            .IsRequired()
            .HasMaxLength(16)
            .HasConversion<string>();
        entity.Property(snippet => snippet.Header).IsRequired().HasMaxLength(1024);
        entity.Property(snippet => snippet.HeadingPath).IsRequired().HasMaxLength(2048);
        entity.Property(snippet => snippet.Language).HasMaxLength(64);
        entity.Property(snippet => snippet.Tag).HasMaxLength(256);
        entity.Property(snippet => snippet.PrecedingText);
        entity.Property(snippet => snippet.Content).IsRequired();
        entity.Property(snippet => snippet.EmbeddingPayload).IsRequired();
        entity.Property(snippet => snippet.Identifiers).HasColumnType("text[]");
        entity.Property(snippet => snippet.ContentHash).IsRequired().HasMaxLength(64);
        entity.Property(snippet => snippet.Ordinal).IsRequired();
        entity.Property(snippet => snippet.TokenCount).IsRequired();

        entity.Property(snippet => snippet.Embedding).HasColumnType("halfvec(768)");
        entity.Property(snippet => snippet.EmbeddingModel).HasMaxLength(128);
        entity.Property(snippet => snippet.EmbeddingStartedAt);

        entity
            .Property(snippet => snippet.SearchVector)
            .HasColumnType("tsvector")
            .HasComputedColumnSql(
                """
                setweight(to_tsvector('english', coalesce(heading_path, '')), 'A') ||
                setweight(to_tsvector('simple',  coalesce(zeeq.immutable_array_to_string(identifiers, ' '), '')), 'B') ||
                setweight(to_tsvector('simple',  coalesce(language, '') || ' ' || coalesce(tag, '')), 'B') ||
                setweight(to_tsvector('english', coalesce(content, '')), 'D')
                """,
                stored: true
            );

        entity.Property(snippet => snippet.CreatedAt).IsRequired();
        entity.Property(snippet => snippet.UpdatedAt).IsRequired();

        // Reconciliation lookup during ReplaceForDocumentAsync (public variant). This index only
        // needs to support "fetch every snippet row for this document" — PostgresPublicDocumentSnippetStore
        // loads that full set, then matches/diffs by (Kind, ContentHash, Ordinal) in memory via
        // existing.ToDictionary(...), the same pattern LibraryDocumentSnippetConfiguration uses. The
        // composite reconcile key is never used in a SQL predicate, so it deliberately does not need
        // to appear in this index; see the analogous NOTE on the private table's index above for the
        // no-unique-constraint rationale, which applies identically here.
        entity
            .HasIndex(snippet => new { snippet.PublicSourceId, snippet.DocumentId })
            .HasDatabaseName("ix_docs_public_document_snippets_document");

        // FK to the owning public document, ON DELETE CASCADE.
        entity
            .HasOne<DocsPublicDocument>()
            .WithMany()
            .HasForeignKey(snippet => snippet.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
