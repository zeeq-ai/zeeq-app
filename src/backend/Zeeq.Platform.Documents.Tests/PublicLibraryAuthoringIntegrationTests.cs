using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Zeeq.Core.Auditing;
using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Data.Postgres;
using Zeeq.Platform.Documents.Editing;
using Zeeq.Testing;
using Zeeq.Testing.Biomes;

namespace Zeeq.Platform.Documents.Tests;

/// <summary>Checks public-source subscriptions with local authoring against PostgreSQL.</summary>
[Property("integration", "true")]
[Property("testcontainer", "true")]
[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class PublicLibraryAuthoringIntegrationTests(PgDatabaseFixture postgres)
    : PgTransactionalTestBase(postgres)
{
    [Test]
    public async Task LocalCreateAndLaterPublicCollision_AreVisibleWithoutChangingSharedSource()
    {
        await TestBiome.Start(_context, out var seed).AddPublicSource(out var source).BuildAsync();
        var now = DateTimeOffset.UtcNow;
        var library = new Library
        {
            Id = SeedContext.NewId("library"),
            OrganizationId = seed.Organization.Id,
            Name = "subscribed",
            PublicSourceId = source.Id,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var other = new Library
        {
            Id = SeedContext.NewId("library"),
            OrganizationId = seed.Organization.Id,
            Name = "other",
            PublicSourceId = source.Id,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _context.Libraries.AddRange(library, other);
        DocsPublicDocument PublicDoc(string path, string content) =>
            new()
            {
                Id = SeedContext.NewId("doc"),
                PublicSourceId = source.Id,
                Path = path,
                Title = "Shared",
                TitleNormalized = "shared",
                Content = content,
                ContentHash = DocumentContentHash.Compute(content),
                CreatedAt = now,
                UpdatedAt = now,
            };
        var existing = PublicDoc("/existing.md", "shared-existing");
        _context.DocsPublicDocuments.Add(existing);
        await _context.SaveChangesAsync();
        var settings = new AppSettings
        {
            Database = new DatabaseSettings
            {
                ConnectionString = _context.Database.GetConnectionString()!,
            },
        };
        var registrations = new ServiceCollection();
        registrations.AddLogging();
        registrations.AddPostgres(settings);
        registrations.AddSingleton(_context);
        using var provider = registrations.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ILibraryDocumentStore>();
        var pub = scope.ServiceProvider.GetRequiredService<IDocsPublicDocumentStore>();
        var sources = scope.ServiceProvider.GetRequiredService<IDocsPublicSourceStore>();
        var editor = DocumentDiffEditor.CreateDefault();
        var service = new DocumentAuthoringService(
            store,
            pub,
            sources,
            editor,
            NullLogger<DocumentAuthoringService>.Instance
        );
        var conflict = await service.CreateAsync(
            seed.Organization.Id,
            library,
            "/existing.md",
            "local",
            "why",
            AuditActor.Worker("test"),
            "client",
            default
        );
        await Assert.That(conflict.Status).IsEqualTo(AuthoringStatus.Exists);
        var created = await service.CreateAsync(
            seed.Organization.Id,
            library,
            "/new.md",
            "local-new",
            "why",
            AuditActor.Worker("test"),
            "client",
            default
        );
        await Assert.That(created.Status).IsEqualTo(AuthoringStatus.Created);
        var listHandler = new ListDocumentsHandler(store, pub, sources);
        var contentHandler = new GetDocumentContentHandler(store, pub, sources);
        var list = await listHandler.HandleAsync(seed.Organization.Id, library.Name, default);
        await Assert
            .That(((Ok<DocumentResponse[]>)list.Result).Value!.Select(d => d.Path))
            .IsEquivalentTo(["/existing.md", "/new.md"]);
        var localRead = await contentHandler.HandleAsync(
            seed.Organization.Id,
            library.Name,
            "/new.md",
            default
        );
        await Assert
            .That(((Ok<DocumentContentResponse>)localRead.Result).Value!.Content)
            .IsEqualTo("local-new");
        var sharedNew = PublicDoc("/new.md", "shared-new");
        _context.DocsPublicDocuments.Add(sharedNew);
        await _context.SaveChangesAsync();
        var localAfter = await contentHandler.HandleAsync(
            seed.Organization.Id,
            library.Name,
            "/new.md",
            default
        );
        var otherAfter = await contentHandler.HandleAsync(
            seed.Organization.Id,
            other.Name,
            "/new.md",
            default
        );
        await Assert
            .That(((Ok<DocumentContentResponse>)localAfter.Result).Value!.Content)
            .IsEqualTo("local-new");
        await Assert
            .That(((Ok<DocumentContentResponse>)otherAfter.Result).Value!.Content)
            .IsEqualTo("shared-new");
        await Assert
            .That(
                (
                    await _context
                        .DocsPublicDocuments.AsNoTracking()
                        .SingleAsync(d => d.Id == sharedNew.Id)
                ).Content
            )
            .IsEqualTo("shared-new");
    }
}
