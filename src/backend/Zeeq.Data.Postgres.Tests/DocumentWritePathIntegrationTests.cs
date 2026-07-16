using Zeeq.Core.Documents;
using Zeeq.Data.Postgres.Documents;
using Zeeq.Testing;
using Zeeq.Testing.EntityGraphs;
using Microsoft.EntityFrameworkCore;

namespace Zeeq.Data.Postgres.Tests;

/// <summary>
/// Integration tests for the markdown document write path.
///
/// Run:
/// dotnet run --project src/backend/Zeeq.Data.Postgres.Tests --output detailed --disable-logo --treenode-filter "/*/*/DocumentWritePathIntegrationTests/*"
/// </summary>
[Property("integration", "true")]
[Property("testcontainer", "true")]
[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class DocumentWritePathIntegrationTests : PgTransactionalTestBase
{
    public DocumentWritePathIntegrationTests(PgDatabaseFixture postgres)
        : base(postgres) { }

    [Test]
    public async Task UpsertDocument_NewDocument_Persisted()
    {
        var (writer, organizationId, library, _) = await CreateWriterWithLibraryAsync();

        var document = await writer.UpsertDocumentAsync(
            organizationId,
            teamId: null,
            library.Id,
            "Guides/Intro",
            "# Hello World\n\nBody content.",
            default
        );
        _context.ChangeTracker.Clear();

        var saved = await _context.LibraryDocuments.SingleAsync(row => row.Id == document.Id);

        await Assert.That(saved.OrganizationId).IsEqualTo(organizationId);
        await Assert.That(saved.LibraryId).IsEqualTo(library.Id);
        await Assert.That(saved.Path).IsEqualTo("/guides/intro.md");
        await Assert.That(saved.Title).IsEqualTo("Hello World");
        await Assert.That(saved.Content).IsEqualTo("# Hello World\n\nBody content.");
    }

    [Test]
    public async Task UpsertDocument_FrontMatter_PreservesFullMarkdownSource()
    {
        var (writer, organizationId, library, _) = await CreateWriterWithLibraryAsync();
        const string markdown = """
            ---
            keywords: alpha, beta
            ---

            # Guide

            Body content.
            """;

        var document = await writer.UpsertDocumentAsync(
            organizationId,
            teamId: null,
            library.Id,
            "/guides/front-matter.md",
            markdown,
            default
        );
        _context.ChangeTracker.Clear();

        var saved = await _context.LibraryDocuments.SingleAsync(row => row.Id == document.Id);

        await Assert.That(saved.Content).IsEqualTo(markdown);
        await Assert.That(saved.Keywords).IsEquivalentTo(["alpha", "beta"]);
    }

    [Test]
    public async Task UpsertDocument_SameContent_PreservesUpdatedAt()
    {
        var (writer, organizationId, library, _) = await CreateWriterWithLibraryAsync();
        var original = await writer.UpsertDocumentAsync(
            organizationId,
            teamId: null,
            library.Id,
            "/guides/no-op.md",
            "# Stable\n\nSame body.",
            default
        );
        _context.ChangeTracker.Clear();

        var unchanged = await writer.UpsertDocumentAsync(
            organizationId,
            teamId: null,
            library.Id,
            "/guides/no-op.md",
            "# Stable\n\nSame body.",
            default
        );

        await Assert.That(unchanged.Id).IsEqualTo(original.Id);
        await Assert
            .That(unchanged.UpdatedAt)
            .IsEqualTo(original.UpdatedAt.TruncateToPostgresPrecision());
        await Assert.That(unchanged.ContentHash).IsEqualTo(original.ContentHash);
    }

    [Test]
    public async Task UpsertDocument_UpdatedContent_SetsProcessingPending()
    {
        var (writer, organizationId, library, _) = await CreateWriterWithLibraryAsync();
        await writer.UpsertDocumentAsync(
            organizationId,
            teamId: null,
            library.Id,
            "/guides/update.md",
            "# Old\n\nOld body.",
            default
        );
        _context.ChangeTracker.Clear();

        var updated = await writer.UpsertDocumentAsync(
            organizationId,
            teamId: null,
            library.Id,
            "/guides/update.md",
            "# New\n\nNew body.",
            default
        );

        await Assert.That(updated.Title).IsEqualTo("New");
        await Assert.That(updated.ProcessingStatus).IsEqualTo(DocumentProcessingStatus.Pending);
    }

    [Test]
    public async Task UpsertDocument_UpdatedTeam_PreservesOriginalTeam()
    {
        var (writer, organizationId, library, rootTeamId) = await CreateWriterWithLibraryAsync();
        var original = await writer.UpsertDocumentAsync(
            organizationId,
            rootTeamId,
            library.Id,
            "/guides/team.md",
            "# Original\n\nOriginal body.",
            default
        );
        _context.ChangeTracker.Clear();

        var updated = await writer.UpsertDocumentAsync(
            organizationId,
            teamId: null,
            library.Id,
            "/guides/team.md",
            "# Updated\n\nUpdated body.",
            default
        );

        await Assert.That(updated.Id).IsEqualTo(original.Id);
        await Assert.That(updated.TeamId).IsEqualTo(rootTeamId);
    }

    [Test]
    public async Task UpsertDocument_Title_NormalizesCorrectly()
    {
        var (writer, organizationId, library, _) = await CreateWriterWithLibraryAsync();

        var document = await writer.UpsertDocumentAsync(
            organizationId,
            teamId: null,
            library.Id,
            "/guides/title.md",
            "# My Doc (v2)\n\nBody.",
            default
        );

        await Assert.That(document.TitleNormalized).IsEqualTo("my doc v2");
    }

    [Test]
    public async Task UpsertDocument_Keywords_NormalizesDedupes()
    {
        var (writer, organizationId, library, _) = await CreateWriterWithLibraryAsync();

        var document = await writer.UpsertDocumentAsync(
            organizationId,
            teamId: null,
            library.Id,
            "/guides/keywords.md",
            "---\nkeywords: AI, ai, C#(sharp)\n---\n# Keywords\n\nBody.",
            default
        );

        await Assert.That(document.Keywords).IsEquivalentTo(["ai", "csharp"]);
    }

    [Test]
    public async Task UpsertDocument_Headings_PreservesAuthoredText()
    {
        var (writer, organizationId, library, _) = await CreateWriterWithLibraryAsync();

        var document = await writer.UpsertDocumentAsync(
            organizationId,
            teamId: null,
            library.Id,
            "/guides/headings.md",
            "# Title\n\n## Mixed CASE Heading\n\nBody.",
            default
        );

        await Assert.That(document.Headings).Contains("Mixed CASE Heading");
    }

    [Test]
    public async Task UpsertDocument_Content_ProducesTokenCount()
    {
        var (writer, organizationId, library, _) = await CreateWriterWithLibraryAsync();

        var document = await writer.UpsertDocumentAsync(
            organizationId,
            teamId: null,
            library.Id,
            "/guides/tokens.md",
            "# Tokens\n\nThis body has countable content.",
            default
        );

        await Assert.That(document.TokenCount).IsGreaterThan(0);
    }

    [Test]
    public async Task UpsertDocument_DuplicatePath_UpdatesExistingDocument()
    {
        var (writer, organizationId, library, _) = await CreateWriterWithLibraryAsync();
        var original = await writer.UpsertDocumentAsync(
            organizationId,
            teamId: null,
            library.Id,
            "Guides/Duplicate",
            "# Original\n\nFirst body.",
            default
        );
        _context.ChangeTracker.Clear();

        var updated = await writer.UpsertDocumentAsync(
            organizationId,
            teamId: null,
            library.Id,
            "/guides/duplicate.md",
            "# Updated\n\nSecond body.",
            default
        );
        var count = await _context.LibraryDocuments.CountAsync(row =>
            row.OrganizationId == organizationId
            && row.LibraryId == library.Id
            && row.Path == "/guides/duplicate.md"
        );

        await Assert.That(updated.Id).IsEqualTo(original.Id);
        await Assert.That(updated.Title).IsEqualTo("Updated");
        await Assert.That(count).IsEqualTo(1);
    }

    private async Task<(
        LibraryDocumentWriteService Writer,
        string OrganizationId,
        Library Library,
        string RootTeamId
    )> CreateWriterWithLibraryAsync()
    {
        var seed = await EntityGraph.AddGeneratedSeed(_context).BuildAsync();
        var store = new PostgresLibraryDocumentStore(_context, new DocumentSearchScope());
        var library = await CreateLibraryAsync(store, seed.Organization.Id, "docs");
        var writer = new LibraryDocumentWriteService(store);

        return (writer, seed.Organization.Id, library, seed.RootTeam.Id);
    }

    private static Task<Library> CreateLibraryAsync(
        PostgresLibraryDocumentStore store,
        string organizationId,
        string name
    )
    {
        var now = DateTimeOffset.UtcNow;
        return store.CreateLibraryAsync(
            new Library
            {
                Id = SeedContext.NewId("library"),
                OrganizationId = organizationId,
                Name = name,
                CreatedAt = now,
                UpdatedAt = now,
            },
            default
        );
    }
}
