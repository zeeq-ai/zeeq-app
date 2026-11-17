using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Zeeq.Core.Documents;
using Zeeq.Testing;
using Zeeq.Testing.Biomes;

namespace Zeeq.Data.Postgres.Tests;

/// <summary>Verifies ownership backfill against an isolated pre-migration database.</summary>
public sealed class LibraryDocumentAuthoringMigrationTests
{
    [Test]
    public async Task Backfill_UsesRowProvenanceAndDefaultsOtherRowsToManual()
    {
        if (
            !string.IsNullOrEmpty(
                Environment.GetEnvironmentVariable("ZEEQ_TEST_POSTGRES_CONNECTION_STRING")
            )
        )
        {
            throw new InvalidOperationException("Migration test requires its own test container.");
        }

        await using var postgres = new PgDatabaseFixture();
        await postgres.InitializeAsync();
        await using var db = postgres.CreateContext();
        await TestBiome.Start(db, out var seed).BuildAsync();
        var now = DateTimeOffset.UtcNow;
        var library = new Library
        {
            Id = SeedContext.NewId("library"),
            OrganizationId = seed.Organization.Id,
            Name = "migration test",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Libraries.Add(library);
        LibraryDocument Row(string path) =>
            new()
            {
                Id = SeedContext.NewId("document"),
                OrganizationId = seed.Organization.Id,
                LibraryId = library.Id,
                Path = path,
                Title = path,
                TitleNormalized = path,
                Content = "body",
                CreatedAt = now,
                UpdatedAt = now,
            };
        var manual = Row("/manual.md");
        var stamped = Row("/stamped.md");
        stamped.SyncRunId = "run-1";
        var external = Row("/external.md");
        external.SourceExternalId = "page-1";
        var origin = Row("/origin.md");
        db.LibraryDocuments.AddRange(manual, stamped, external, origin);
        await db.SaveChangesAsync();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261112202912_AddCodeReviewEvaluations");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE zeeq.docs_library_documents SET source_origin = '{{\"Kind\":\"GitHub\",\"RepoRef\":\"https://github.com/a/b\"}}'::jsonb WHERE id = {origin.Id}"
        );
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE zeeq.docs_libraries SET source_kind = {"GitHub"} WHERE id = {library.Id}"
        );
        await migrator.MigrateAsync();
        db.ChangeTracker.Clear();
        var rows = await db.LibraryDocuments.Where(d => d.LibraryId == library.Id).ToArrayAsync();
        await Assert
            .That(rows.Single(d => d.Id == manual.Id).Authoring)
            .IsEqualTo(LibraryDocumentAuthoring.Manual);
        await Assert
            .That(rows.Single(d => d.Id == stamped.Id).Authoring)
            .IsEqualTo(LibraryDocumentAuthoring.Synced);
        await Assert
            .That(rows.Single(d => d.Id == external.Id).Authoring)
            .IsEqualTo(LibraryDocumentAuthoring.Synced);
        await Assert
            .That(rows.Single(d => d.Id == origin.Id).Authoring)
            .IsEqualTo(LibraryDocumentAuthoring.Synced);
    }
}
