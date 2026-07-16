using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Platform.Documents.Tests;

// NOTE: Per-test store construction is intentional — tests vary between no-library, library-only,
// and library+document setups, so a shared constructor would silently break the no-library 404
// tests and obscure what each test actually depends on.
public sealed class DocumentEndpointHandlerTests
{
    [Test]
    public async Task CreateLibrary_WithUnsafeName_ReturnsBadRequest()
    {
        var handler = new CreateLibraryHandler(
            new TestLibraryDocumentStore(),
            new NotSupportedPublicSourceStore(),
            new NotSupportedCodeRepositoryStore()
        );

        var result = await handler.HandleAsync(
            "org_123",
            new CreateLibraryRequest { Name = "bad name" },
            TestUser(),
            CancellationToken.None
        );

        var badRequest = result.Result as BadRequest<LibraryError>;

        await Assert.That(badRequest).IsNotNull();
        await Assert.That(badRequest!.Value!.Message).Contains("letters, numbers");
    }

    [Test]
    public async Task CreateLibrary_PublicSource_NewUrl_CreatesSourceAndLibrary()
    {
        var libraries = new TestLibraryDocumentStore();
        var publicSources = new TestPublicSourceStore();
        var handler = new CreateLibraryHandler(
            libraries,
            publicSources,
            new NotSupportedCodeRepositoryStore()
        );

        var result = await handler.HandleAsync(
            "org_123",
            new CreateLibraryRequest
            {
                Name = "tasklet-docs",
                Source = new CreateLibrarySourceRequest
                {
                    Kind = LibrarySourceKindRequest.Public,
                    RepoUrl = "https://github.com/CharlieDigital/Tasklet",
                    IncludeFilters = [".agents/context/*.md"],
                },
            },
            TestUser(),
            CancellationToken.None
        );

        var created = result.Result as Created<LibraryResponse>;
        await Assert.That(created).IsNotNull();
        await Assert.That(created!.Value!.Source).IsNotNull();
        await Assert.That(created.Value.Source!.Kind).IsEqualTo("Public");
        await Assert
            .That(created.Value.Source.RepoUrl)
            .IsEqualTo("https://github.com/CharlieDigital/Tasklet.git");
        await Assert
            .That(created.Value.Source.IncludeFilters)
            .IsEquivalentTo([".agents/context/*.md"]);

        await Assert.That(publicSources.Sources).Count().IsEqualTo(1);
        var library = libraries.Libraries.Single();
        await Assert.That(library.PublicSourceId).IsEqualTo(publicSources.Sources.Single().Id);
        await Assert.That(library.SourceKind).IsNull();
    }

    [Test]
    public async Task CreateLibrary_PublicSource_ExistingUrl_ReusesSourceRow()
    {
        var libraries = new TestLibraryDocumentStore();
        var publicSources = new TestPublicSourceStore();
        publicSources.Sources.Add(
            new DocsPublicSource
            {
                Id = "pubsrc_existing",
                RepoUrl = "https://github.com/CharlieDigital/Tasklet.git",
                Name = "Tasklet",
                SyncStatus = "idle",
                Status = "active",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            }
        );
        var handler = new CreateLibraryHandler(
            libraries,
            publicSources,
            new NotSupportedCodeRepositoryStore()
        );

        var result = await handler.HandleAsync(
            "org_456",
            new CreateLibraryRequest
            {
                Name = "tasklet-docs-2",
                Source = new CreateLibrarySourceRequest
                {
                    Kind = LibrarySourceKindRequest.Public,
                    // Trailing-slash + no .git — must normalize to the same URL.
                    RepoUrl = "https://github.com/CharlieDigital/Tasklet/",
                },
            },
            TestUser(),
            CancellationToken.None
        );

        await Assert.That(result.Result).IsTypeOf<Created<LibraryResponse>>();
        // Dedup: no second source row was created.
        await Assert.That(publicSources.Sources).Count().IsEqualTo(1);
        await Assert.That(libraries.Libraries.Single().PublicSourceId).IsEqualTo("pubsrc_existing");
    }

    [Test]
    public async Task CreateLibrary_PublicSource_ImplausibleUrl_ReturnsBadRequest()
    {
        var handler = new CreateLibraryHandler(
            new TestLibraryDocumentStore(),
            new TestPublicSourceStore(),
            new NotSupportedCodeRepositoryStore()
        );

        var result = await handler.HandleAsync(
            "org_123",
            new CreateLibraryRequest
            {
                Name = "docs",
                Source = new CreateLibrarySourceRequest
                {
                    Kind = LibrarySourceKindRequest.Public,
                    RepoUrl = "not-a-url",
                },
            },
            TestUser(),
            CancellationToken.None
        );

        await Assert.That(result.Result).IsTypeOf<BadRequest<LibraryError>>();
    }

    [Test]
    public async Task CreateLibrary_PrivateSource_ResolvesConfiguredRepository()
    {
        var libraries = new TestLibraryDocumentStore();
        var repositories = new TestCodeRepositoryStore();
        repositories.Repositories.Add(
            new CodeRepository
            {
                Id = "repo_1",
                OrganizationId = "org_123",
                Provider = "github",
                OwnerQualifiedName = "zeeq-ai/zeeq",
                DisplayName = "zeeq",
                Enabled = true,
            }
        );
        var handler = new CreateLibraryHandler(
            libraries,
            new NotSupportedPublicSourceStore(),
            repositories
        );

        var result = await handler.HandleAsync(
            "org_123",
            new CreateLibraryRequest
            {
                Name = "zeeq-docs",
                Source = new CreateLibrarySourceRequest
                {
                    Kind = LibrarySourceKindRequest.Private,
                    RepositoryId = "repo_1",
                    ExcludeFilters = ["**/bin/**"],
                },
            },
            TestUser(),
            CancellationToken.None
        );

        var created = result.Result as Created<LibraryResponse>;
        await Assert.That(created).IsNotNull();
        await Assert.That(created!.Value!.Source!.Kind).IsEqualTo("Private");
        await Assert
            .That(created.Value.Source.RepoUrl)
            .IsEqualTo("https://github.com/zeeq-ai/zeeq.git");

        var library = libraries.Libraries.Single();
        await Assert.That(library.SourceKind).IsEqualTo("GitHub");
        await Assert.That(library.PublicSourceId).IsNull();
    }

    [Test]
    public async Task CreateLibrary_PrivateSource_UnknownRepositoryId_ReturnsBadRequest()
    {
        var handler = new CreateLibraryHandler(
            new TestLibraryDocumentStore(),
            new NotSupportedPublicSourceStore(),
            new TestCodeRepositoryStore()
        );

        var result = await handler.HandleAsync(
            "org_123",
            new CreateLibraryRequest
            {
                Name = "zeeq-docs",
                Source = new CreateLibrarySourceRequest
                {
                    Kind = LibrarySourceKindRequest.Private,
                    RepositoryId = "does_not_exist",
                },
            },
            TestUser(),
            CancellationToken.None
        );

        await Assert.That(result.Result).IsTypeOf<BadRequest<LibraryError>>();
    }

    [Test]
    public async Task UpdateLibrary_WithMissingDescription_PreservesExistingDescription()
    {
        var store = new TestLibraryDocumentStore();
        store.Libraries.Add(TestLibrary(description: "Keep me"));
        var handler = new UpdateLibraryHandler(store, new NotSupportedPublicSourceStore());

        var result = await handler.HandleAsync(
            "org_123",
            "kb",
            new UpdateLibraryRequest { Name = "kb-renamed" },
            TestUser(),
            CancellationToken.None
        );

        var ok = result.Result as Ok<LibraryResponse>;

        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value!.Name).IsEqualTo("kb-renamed");
        await Assert.That(ok.Value.Description).IsEqualTo("Keep me");
        await Assert.That(store.UpdatedLibrary!.Description).IsEqualTo("Keep me");
    }

    [Test]
    public async Task ListDocuments_WithLibrary_ReturnsDocuments()
    {
        var store = new TestLibraryDocumentStore();
        store.Libraries.Add(TestLibrary());
        store.Documents.Add(TestDocument());
        var handler = new ListDocumentsHandler(
            store,
            new NotSupportedPublicDocumentStore(),
            new NotSupportedPublicSourceStore()
        );

        var result = await handler.HandleAsync("org_123", "kb", CancellationToken.None);

        var ok = result.Result as Ok<DocumentResponse[]>;

        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value!).HasSingleItem();
        await Assert.That(ok.Value![0].Path).IsEqualTo("/docs/guide.md");
    }

    [Test]
    public async Task UpsertDocument_WithRelativeSegment_ReturnsBadRequest()
    {
        var store = new TestLibraryDocumentStore();
        store.Libraries.Add(TestLibrary());
        var handler = new UpsertDocumentHandler(store, new LibraryDocumentWriteService(store));

        var result = await handler.HandleAsync(
            "org_123",
            "kb",
            new UpsertDocumentRequest { Path = "docs/../guide.md", Content = "# Guide" },
            TestUser(teamId: "team_123"),
            CancellationToken.None
        );

        var badRequest = result.Result as BadRequest<DocumentError>;

        await Assert.That(badRequest).IsNotNull();
        await Assert.That(badRequest!.Value!.Message).Contains("Relative path segments");
        await Assert.That(store.UpsertedDocument).IsNull();
    }

    [Test]
    public async Task DeleteDocument_NormalizesPathAndReturnsNoContent()
    {
        var store = new TestLibraryDocumentStore();
        store.Libraries.Add(TestLibrary());
        var handler = new DeleteDocumentHandler(store);

        var result = await handler.HandleAsync(
            "org_123",
            "kb",
            "Docs/Guide",
            CancellationToken.None
        );

        await Assert.That(result.Result is NoContent).IsTrue();
        await Assert.That(store.DeletedDocumentPath).IsEqualTo("/docs/guide.md");
    }

    // ── GetDocumentContentHandler tests ─────────────────────────────────

    [Test]
    public async Task GetContent_ExistingPath_ReturnsContent()
    {
        var store = new TestLibraryDocumentStore();
        store.Libraries.Add(TestLibrary());
        store.Documents.Add(TestDocument());
        var handler = new GetDocumentContentHandler(
            store,
            new NotSupportedPublicDocumentStore(),
            new NotSupportedPublicSourceStore()
        );

        var result = await handler.HandleAsync(
            "org_123",
            "kb",
            "/docs/guide.md",
            CancellationToken.None
        );

        var ok = result.Result as Ok<DocumentContentResponse>;

        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value!.Content).IsEqualTo("# Guide");
        await Assert.That(ok.Value.Origin).IsEqualTo("local");
    }

    [Test]
    public async Task GetContent_MissingPath_Returns400()
    {
        var store = new TestLibraryDocumentStore();
        store.Libraries.Add(TestLibrary());
        var handler = new GetDocumentContentHandler(
            store,
            new NotSupportedPublicDocumentStore(),
            new NotSupportedPublicSourceStore()
        );

        var result = await handler.HandleAsync("org_123", "kb", "", CancellationToken.None);

        await Assert.That(result.Result is BadRequest<DocumentError>).IsTrue();
    }

    [Test]
    public async Task GetContent_UnknownPath_Returns404()
    {
        var store = new TestLibraryDocumentStore();
        store.Libraries.Add(TestLibrary());
        var handler = new GetDocumentContentHandler(
            store,
            new NotSupportedPublicDocumentStore(),
            new NotSupportedPublicSourceStore()
        );

        var result = await handler.HandleAsync(
            "org_123",
            "kb",
            "/nonexistent.md",
            CancellationToken.None
        );

        await Assert.That(result.Result is NotFound).IsTrue();
    }

    [Test]
    public async Task GetContent_UnknownLibrary_Returns404()
    {
        var store = new TestLibraryDocumentStore();
        var handler = new GetDocumentContentHandler(
            store,
            new NotSupportedPublicDocumentStore(),
            new NotSupportedPublicSourceStore()
        );

        var result = await handler.HandleAsync(
            "org_123",
            "unknown-lib",
            "/docs/guide.md",
            CancellationToken.None
        );

        await Assert.That(result.Result is NotFound).IsTrue();
    }

    // ── PreviewDocumentParseHandler tests ───────────────────────────────

    [Test]
    public async Task PreviewParse_ExistingPath_ReturnsParsedTitleKeywordsAndSnippets()
    {
        var store = new TestLibraryDocumentStore();
        store.Libraries.Add(TestLibrary());
        var document = TestDocument();
        document.Content =
            "---\nkeywords: alpha, beta\n---\n\n# Guide\n\nSection body long enough to clear the minimum section length threshold for composing a snippet.\n";
        store.Documents.Add(document);
        var handler = new PreviewDocumentParseHandler(
            store,
            new NotSupportedPublicDocumentStore(),
            new NotSupportedPublicSourceStore(),
            new SnippetIndexingSettings()
        );

        var result = await handler.HandleAsync(
            "org_123",
            "kb",
            "/docs/guide.md",
            CancellationToken.None
        );

        var ok = result.Result as Ok<DocumentParsePreviewResponse>;

        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value!.Title).IsEqualTo("Guide");
        await Assert.That(ok.Value.Keywords).IsEquivalentTo(["alpha", "beta"]);
        await Assert.That(ok.Value.Headings).IsEquivalentTo(["Guide"]);
        await Assert.That(ok.Value.Snippets).Count().IsEqualTo(1);
        await Assert.That(ok.Value.Snippets[0].Kind).IsEqualTo("section");
    }

    [Test]
    public async Task PreviewParse_MissingPath_Returns400()
    {
        var store = new TestLibraryDocumentStore();
        store.Libraries.Add(TestLibrary());
        var handler = new PreviewDocumentParseHandler(
            store,
            new NotSupportedPublicDocumentStore(),
            new NotSupportedPublicSourceStore(),
            new SnippetIndexingSettings()
        );

        var result = await handler.HandleAsync("org_123", "kb", "", CancellationToken.None);

        await Assert.That(result.Result is BadRequest<DocumentError>).IsTrue();
    }

    [Test]
    public async Task PreviewParse_UnknownPath_Returns404()
    {
        var store = new TestLibraryDocumentStore();
        store.Libraries.Add(TestLibrary());
        var handler = new PreviewDocumentParseHandler(
            store,
            new NotSupportedPublicDocumentStore(),
            new NotSupportedPublicSourceStore(),
            new SnippetIndexingSettings()
        );

        var result = await handler.HandleAsync(
            "org_123",
            "kb",
            "/nonexistent.md",
            CancellationToken.None
        );

        await Assert.That(result.Result is NotFound).IsTrue();
    }

    // ── SearchDocumentsHandler tests ────────────────────────────────────

    [Test]
    public async Task Search_Query_ReturnsRankedResults()
    {
        var store = new TestLibraryDocumentStore();
        store.Libraries.Add(TestLibrary());
        store.Documents.Add(TestDocument());
        var handler = new SearchDocumentsHandler(store);

        var result = await handler.HandleAsync(
            "org_123",
            "kb",
            "guide",
            10,
            CancellationToken.None
        );

        var ok = result.Result as Ok<DocumentSearchResultResponse[]>;

        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value!).HasSingleItem();
        await Assert.That(ok.Value![0].Library).IsEqualTo("kb");
        await Assert.That(ok.Value[0].Title).IsEqualTo("Guide");
        await Assert.That(ok.Value[0].Path).IsEqualTo("/docs/guide.md");
    }

    [Test]
    public async Task Search_EmptyQuery_Returns400()
    {
        var store = new TestLibraryDocumentStore();
        store.Libraries.Add(TestLibrary());
        var handler = new SearchDocumentsHandler(store);

        var result = await handler.HandleAsync("org_123", "kb", "", null, CancellationToken.None);

        await Assert.That(result.Result is BadRequest<DocumentError>).IsTrue();
    }

    [Test]
    public async Task Search_LimitCappedAt50()
    {
        var store = new TestLibraryDocumentStore();
        store.Libraries.Add(TestLibrary());
        var handler = new SearchDocumentsHandler(store);

        // This will throw NotSupportedException from the store, but the handler
        // will clamp the limit first. Verify via the stored limit.
        try
        {
            await handler.HandleAsync("org_123", "kb", "test", 999, CancellationToken.None);
        }
        catch (NotSupportedException)
        {
            // Expected: store.SearchAsync throws.
        }

        await Assert.That(store.LastSearchLimit).IsEqualTo(50);
    }

    // ── ListDocuments_IncludesOrigin ─────────────────────────────────────

    [Test]
    public async Task ListDocuments_IncludesOrigin()
    {
        var store = new TestLibraryDocumentStore();
        store.Libraries.Add(TestLibrary());
        store.Documents.Add(TestDocument());
        var handler = new ListDocumentsHandler(
            store,
            new NotSupportedPublicDocumentStore(),
            new NotSupportedPublicSourceStore()
        );

        var result = await handler.HandleAsync("org_123", "kb", CancellationToken.None);

        var ok = result.Result as Ok<DocumentResponse[]>;

        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value![0].Origin).IsEqualTo("local");
    }

    // ── SetDocumentReviewExclusionHandler tests ─────────────────────────

    [Test]
    public async Task SetReviewExclusion_LocalDocument_TogglesFlag()
    {
        var store = new TestLibraryDocumentStore();
        store.Libraries.Add(TestLibrary());
        store.Documents.Add(TestDocument());
        var handler = new SetDocumentReviewExclusionHandler(store);

        var result = await handler.HandleAsync(
            "org_123",
            "kb",
            new SetDocumentReviewExclusionRequest("doc_123", Excluded: true),
            CancellationToken.None
        );

        var ok = result.Result as Ok<DocumentResponse>;

        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value!.ExcludedFromCodeReviews).IsTrue();
        await Assert.That(store.Documents.Single().ExcludedFromCodeReviews).IsTrue();

        var cleared = await handler.HandleAsync(
            "org_123",
            "kb",
            new SetDocumentReviewExclusionRequest("doc_123", Excluded: false),
            CancellationToken.None
        );

        var clearedOk = cleared.Result as Ok<DocumentResponse>;

        await Assert.That(clearedOk).IsNotNull();
        await Assert.That(clearedOk!.Value!.ExcludedFromCodeReviews).IsFalse();
        await Assert.That(store.Documents.Single().ExcludedFromCodeReviews).IsFalse();
    }

    [Test]
    public async Task SetReviewExclusion_SyncedDocument_Returns400()
    {
        // Guards the v1 scope rule: a sync run owns synced documents' lifecycle, so the flag
        // is rejected rather than silently fighting the next ingest pass.
        var store = new TestLibraryDocumentStore();
        store.Libraries.Add(TestLibrary());
        var synced = TestDocument();
        synced.SyncRunId = "run_123";
        store.Documents.Add(synced);
        var handler = new SetDocumentReviewExclusionHandler(store);

        var result = await handler.HandleAsync(
            "org_123",
            "kb",
            new SetDocumentReviewExclusionRequest("doc_123", Excluded: true),
            CancellationToken.None
        );

        await Assert.That(result.Result is BadRequest<DocumentError>).IsTrue();
        await Assert.That(store.Documents.Single().ExcludedFromCodeReviews).IsFalse();
    }

    [Test]
    public async Task SetReviewExclusion_UnknownDocumentId_Returns404()
    {
        var store = new TestLibraryDocumentStore();
        store.Libraries.Add(TestLibrary());
        var handler = new SetDocumentReviewExclusionHandler(store);

        var result = await handler.HandleAsync(
            "org_123",
            "kb",
            new SetDocumentReviewExclusionRequest("missing-doc", Excluded: true),
            CancellationToken.None
        );

        await Assert.That(result.Result is NotFound).IsTrue();
    }

    [Test]
    public async Task SetReviewExclusion_MissingDocumentId_Returns400()
    {
        var store = new TestLibraryDocumentStore();
        store.Libraries.Add(TestLibrary());
        var handler = new SetDocumentReviewExclusionHandler(store);

        var result = await handler.HandleAsync(
            "org_123",
            "kb",
            new SetDocumentReviewExclusionRequest("  ", Excluded: true),
            CancellationToken.None
        );

        await Assert.That(result.Result is BadRequest<DocumentError>).IsTrue();
    }

    // ── RenameDocumentHandler tests ─────────────────────────────────────

    [Test]
    public async Task Rename_ValidTarget_MovesAndRecordsAlias()
    {
        var store = new TestLibraryDocumentStore();
        store.Libraries.Add(TestLibrary());
        store.Documents.Add(TestDocument());
        var handler = new RenameDocumentHandler(store);

        var result = await handler.HandleAsync(
            "org_123",
            "kb",
            new RenameDocumentRequest("/docs/guide.md", "/docs/new-guide.md"),
            CancellationToken.None
        );

        var ok = result.Result as Ok<DocumentResponse>;

        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value!.Path).IsEqualTo("/docs/new-guide.md");
        await Assert.That(ok.Value.Id).IsEqualTo("doc_123");
        // Verify the alias was recorded.
        await Assert.That(store.MovedDocument!.PreviousPaths).Contains("/docs/guide.md");
    }

    [Test]
    public async Task Rename_OldPath_StillResolves()
    {
        var store = new TestLibraryDocumentStore();
        store.Libraries.Add(TestLibrary());
        store.Documents.Add(TestDocument());
        var handler = new RenameDocumentHandler(store);

        await handler.HandleAsync(
            "org_123",
            "kb",
            new RenameDocumentRequest("/docs/guide.md", "/docs/new-guide.md"),
            CancellationToken.None
        );

        // Resolving by the old path should still find the document.
        var doc = await store.GetByPathAsync(
            "org_123",
            "lib_123",
            "/docs/guide.md",
            CancellationToken.None
        );

        await Assert.That(doc).IsNotNull();
        await Assert.That(doc!.Path).IsEqualTo("/docs/new-guide.md");
    }

    [Test]
    public async Task Rename_TargetOccupied_Returns409()
    {
        var store = new TestLibraryDocumentStore();
        store.Libraries.Add(TestLibrary());
        store.Documents.Add(TestDocument());
        store.Documents.Add(
            new LibraryDocument
            {
                Id = "doc_456",
                OrganizationId = "org_123",
                LibraryId = "lib_123",
                Path = "/docs/other.md",
                Title = "Other",
                TitleNormalized = "other",
                Keywords = [],
                Headings = [],
                Content = "# Other",
                ProcessingStatus = DocumentProcessingStatus.Pending,
                TokenCount = 1,
                CreatedAt = DateTimeOffset.UnixEpoch,
                UpdatedAt = DateTimeOffset.UnixEpoch,
            }
        );
        var handler = new RenameDocumentHandler(store);

        var result = await handler.HandleAsync(
            "org_123",
            "kb",
            new RenameDocumentRequest("/docs/guide.md", "/docs/other.md"),
            CancellationToken.None
        );

        await Assert.That(result.Result is Conflict<DocumentError>).IsTrue();
    }

    [Test]
    public async Task Rename_BackToOwnAlias_Succeeds()
    {
        var store = new TestLibraryDocumentStore();
        store.Libraries.Add(TestLibrary());
        store.Documents.Add(TestDocument()); // starts at /docs/guide.md
        var handler = new RenameDocumentHandler(store);

        // Move to a new path, making the original an alias.
        await handler.HandleAsync(
            "org_123",
            "kb",
            new RenameDocumentRequest("/docs/guide.md", "/docs/new-guide.md"),
            CancellationToken.None
        );

        // Move back to the original path (which is now an alias of the same document).
        var result = await handler.HandleAsync(
            "org_123",
            "kb",
            new RenameDocumentRequest("/docs/new-guide.md", "/docs/guide.md"),
            CancellationToken.None
        );

        var ok = result.Result as Ok<DocumentResponse>;
        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value!.Path).IsEqualTo("/docs/guide.md");
        // /docs/new-guide.md becomes the alias; /docs/guide.md must not appear in PreviousPaths.
        await Assert.That(store.MovedDocument!.PreviousPaths).Contains("/docs/new-guide.md");
        await Assert.That(store.MovedDocument!.PreviousPaths).DoesNotContain("/docs/guide.md");
    }

    [Test]
    public async Task Rename_MissingFromOrTo_Returns400()
    {
        var store = new TestLibraryDocumentStore();
        store.Libraries.Add(TestLibrary());
        var handler = new RenameDocumentHandler(store);

        var result = await handler.HandleAsync(
            "org_123",
            "kb",
            new RenameDocumentRequest("", "/docs/target.md"),
            CancellationToken.None
        );

        await Assert.That(result.Result is BadRequest<DocumentError>).IsTrue();
    }

    [Test]
    public async Task Rename_UnknownSource_Returns404()
    {
        var store = new TestLibraryDocumentStore();
        store.Libraries.Add(TestLibrary());
        var handler = new RenameDocumentHandler(store);

        var result = await handler.HandleAsync(
            "org_123",
            "kb",
            new RenameDocumentRequest("/nonexistent.md", "/docs/target.md"),
            CancellationToken.None
        );

        await Assert.That(result.Result is NotFound).IsTrue();
    }

    [Test]
    public async Task Rename_InvalidTargetPath_Returns400()
    {
        var store = new TestLibraryDocumentStore();
        store.Libraries.Add(TestLibrary());
        store.Documents.Add(TestDocument());
        var handler = new RenameDocumentHandler(store);

        var result = await handler.HandleAsync(
            "org_123",
            "kb",
            new RenameDocumentRequest("/docs/guide.md", "../escape.md"),
            CancellationToken.None
        );

        await Assert.That(result.Result is BadRequest<DocumentError>).IsTrue();
    }

    private static ClaimsPrincipal TestUser(string? teamId = null)
    {
        List<Claim> claims = [new(AuthClaims.OrganizationId, "org_123")];
        if (teamId is not null)
        {
            claims.Add(new Claim(AuthClaims.TeamId, teamId));
        }

        return new(new ClaimsIdentity(claims, authenticationType: "test"));
    }

    private static Library TestLibrary(string description = "Knowledge base") =>
        new()
        {
            Id = "lib_123",
            OrganizationId = "org_123",
            TeamId = "team_123",
            Name = "kb",
            Description = description,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };

    private static LibraryDocument TestDocument() =>
        new()
        {
            Id = "doc_123",
            OrganizationId = "org_123",
            TeamId = "team_123",
            LibraryId = "lib_123",
            Path = "/docs/guide.md",
            Title = "Guide",
            TitleNormalized = "guide",
            Keywords = ["docs"],
            Headings = ["Guide"],
            Content = "# Guide",
            ProcessingStatus = DocumentProcessingStatus.Pending,
            TokenCount = 2,
            ContentHash = "hash",
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };

    // NOTE: A near-duplicate TestLibraryDocumentStore exists in Zeeq.Mcp.Documents.Tests.
    // The two differ in GetByPathAsync semantics (exact+alias vs. suffix-only) and in which spy
    // properties they expose. Consolidation into Zeeq.Testing is tracked as a follow-up; it
    // requires reconciling the GetByPathAsync behaviour and updating the MCP tests to pass full paths.
    private sealed class TestLibraryDocumentStore : ILibraryDocumentStore
    {
        public List<Library> Libraries { get; } = [];

        public List<LibraryDocument> Documents { get; } = [];

        public Library? UpdatedLibrary { get; private set; }

        public LibraryDocument? UpsertedDocument { get; private set; }

        public string? DeletedDocumentPath { get; private set; }

        public int? LastSearchLimit { get; private set; }

        public LibraryDocument? MovedDocument { get; private set; }

        public Task<Library?> GetLibraryAsync(
            string organizationId,
            string name,
            CancellationToken ct
        ) =>
            Task.FromResult(
                Libraries.FirstOrDefault(library =>
                    library.OrganizationId == organizationId && library.Name == name
                )
            );

        public Task<IReadOnlyList<Library>> ListLibrariesAsync(
            string organizationId,
            CancellationToken ct
        ) =>
            Task.FromResult<IReadOnlyList<Library>>(
                Libraries.Where(library => library.OrganizationId == organizationId).ToArray()
            );

        public Task<IReadOnlyList<Library>> ListLibrariesByPublicSourceIdAsync(
            string publicSourceId,
            CancellationToken ct
        ) =>
            Task.FromResult<IReadOnlyList<Library>>(
                Libraries.Where(library => library.PublicSourceId == publicSourceId).ToArray()
            );

        public Task<Library> CreateLibraryAsync(Library library, CancellationToken ct)
        {
            Libraries.Add(library);

            return Task.FromResult(library);
        }

        public Task<Library> UpdateLibraryAsync(Library library, CancellationToken ct)
        {
            UpdatedLibrary = library;
            Libraries.RemoveAll(existing => existing.Id == library.Id);
            Libraries.Add(library);

            return Task.FromResult(library);
        }

        public Task DeleteLibraryAsync(
            string organizationId,
            string libraryId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<Library?> GetLibraryByIdAsync(
            string organizationId,
            string libraryId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<Library>> ClaimDueForSyncAsync(int limit, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<LibraryDocument>> ClaimPendingIndexingAsync(
            int limit,
            TimeSpan staleAfter,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task SetProcessingStatusAsync(
            LibraryDocument document,
            DocumentProcessingStatus status,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<Library> UpdateSyncStateAsync(
            string organizationId,
            string libraryId,
            string? syncStatus,
            DateTimeOffset? nextSyncAt,
            DateTimeOffset[] manualTriggerHistory,
            DateTimeOffset? sourceSyncedAt,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<LibraryDocumentUpsertResult> UpsertSyncedDocumentAsync(
            LibraryDocument document,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<int> DeleteUnstampedAsync(
            string organizationId,
            string libraryId,
            string currentSyncRunId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<LibraryDocument> UpsertDocumentAsync(
            LibraryDocument document,
            CancellationToken ct
        )
        {
            UpsertedDocument = document;
            Documents.RemoveAll(existing => existing.Id == document.Id);
            Documents.Add(document);

            return Task.FromResult(document);
        }

        public Task DeleteDocumentAsync(
            string organizationId,
            string libraryId,
            string path,
            CancellationToken ct
        )
        {
            DeletedDocumentPath = path;

            return Task.CompletedTask;
        }

        public Task<LibraryDocument?> GetByPathAsync(
            string organizationId,
            string libraryId,
            string input,
            CancellationToken ct
        )
        {
            var normalized = NormalizeLookupPath(input);

            // Exact path match first.
            var match = Documents.FirstOrDefault(document =>
                document.OrganizationId == organizationId
                && document.LibraryId == libraryId
                && document.Path == normalized
            );

            // Fallback to previous-paths alias lookup (D-4).
            match ??= Documents.FirstOrDefault(document =>
                document.OrganizationId == organizationId
                && document.LibraryId == libraryId
                && document.PreviousPaths.Contains(normalized)
            );

            return Task.FromResult(match);
        }

        public Task<IReadOnlyList<LibraryDocumentMatch>> SearchAsync(
            string organizationId,
            string libraryId,
            string query,
            int limit,
            CancellationToken ct
        )
        {
            LastSearchLimit = limit;

            var matches = Documents
                .Where(document =>
                    document.OrganizationId == organizationId
                    && document.LibraryId == libraryId
                    && (
                        document.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || document.Content.Contains(query, StringComparison.OrdinalIgnoreCase)
                    )
                )
                .Take(limit)
                .Select(document => new LibraryDocumentMatch(
                    document,
                    DocumentMatchType.Both,
                    0.9,
                    0.7
                ))
                .ToArray();

            return Task.FromResult<IReadOnlyList<LibraryDocumentMatch>>(matches);
        }

        public Task<IReadOnlyList<LibraryDocument>> ListDocumentsAsync(
            string organizationId,
            string libraryId,
            CancellationToken ct
        ) =>
            Task.FromResult<IReadOnlyList<LibraryDocument>>(
                Documents
                    .Where(document =>
                        document.OrganizationId == organizationId && document.LibraryId == libraryId
                    )
                    .ToArray()
            );

        public Task<LibraryDocument?> GetByIdAsync(
            string organizationId,
            string libraryId,
            string documentId,
            CancellationToken ct
        ) =>
            Task.FromResult(
                Documents.FirstOrDefault(row =>
                    row.OrganizationId == organizationId
                    && row.LibraryId == libraryId
                    && row.Id == documentId
                )
            );

        public Task<LibraryDocument?> SetCodeReviewExclusionAsync(
            string organizationId,
            string libraryId,
            string documentId,
            bool excluded,
            CancellationToken ct
        )
        {
            var document = Documents.FirstOrDefault(row =>
                row.OrganizationId == organizationId
                && row.LibraryId == libraryId
                && row.Id == documentId
            );

            if (document is null)
            {
                return Task.FromResult<LibraryDocument?>(null);
            }

            document.ExcludedFromCodeReviews = excluded;
            document.UpdatedAt = DateTimeOffset.UtcNow;

            return Task.FromResult<LibraryDocument?>(document);
        }

        public Task<LibraryDocument?> MoveDocumentAsync(
            string organizationId,
            string libraryId,
            string fromPath,
            string toPath,
            CancellationToken ct
        )
        {
            var normalizedFrom = NormalizeLookupPath(fromPath);
            var normalizedTo = DocumentNormalizer.NormalizePath(toPath);

            var doc = Documents.FirstOrDefault(d =>
                d.OrganizationId == organizationId
                && d.LibraryId == libraryId
                && (d.Path == normalizedFrom || d.PreviousPaths.Contains(normalizedFrom))
            );

            if (doc is null)
            {
                return Task.FromResult<LibraryDocument?>(null);
            }

            // Collision check: target path or alias already taken.
            var collides = Documents.Any(d =>
                d.Id != doc.Id
                && d.OrganizationId == organizationId
                && d.LibraryId == libraryId
                && (d.Path == normalizedTo || d.PreviousPaths.Contains(normalizedTo))
            );

            if (collides)
            {
                throw new DuplicateDocumentPathException(normalizedTo);
            }

            doc.RenameTo(normalizedTo);
            MovedDocument = doc;

            return Task.FromResult<LibraryDocument?>(doc);
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
    }

    // NotSupportedException on every member: none of the tests in this file
    // exercise a repository-sourced library (Source is always null / the
    // library always has no PublicSourceId), so these stores are never
    // actually called — see LibraryEndpointMapping.LoadPublicSourceAsync's
    // early-return and CreateLibraryHandler's `Source: null` branch.
    private sealed class NotSupportedPublicSourceStore : IDocsPublicSourceStore
    {
        public Task<DocsPublicSource?> GetByIdAsync(string id, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<DocsPublicSource>> GetByIdsAsync(
            IReadOnlyCollection<string> ids,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<DocsPublicSource?> GetByRepoUrlAsync(string repoUrl, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<DocsPublicSource> CreateAsync(DocsPublicSource source, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<DocsPublicSource> UpdateAsync(DocsPublicSource source, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<DocsPublicSource>> ClaimDueForSyncAsync(
            int limit,
            CancellationToken ct
        ) => throw new NotSupportedException();
    }

    // None of this file's document-endpoint tests exercise a public-source
    // library (they all use TestLibraryDocumentStore-seeded local libraries),
    // so this store's members are never actually called.
    private sealed class NotSupportedPublicDocumentStore : IDocsPublicDocumentStore
    {
        public Task<DocsPublicDocumentUpsertResult> UpsertAsync(
            DocsPublicDocument document,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<DocsPublicDocument>> ListBySourceAsync(
            string publicSourceId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<DocsPublicDocument>> ListSummariesBySourceAsync(
            string publicSourceId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<DocsPublicDocument?> GetByPathAsync(
            string publicSourceId,
            string path,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<int> DeleteUnstampedAsync(
            string publicSourceId,
            string currentSyncRunId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<DocsPublicDocument>> ClaimPendingIndexingAsync(
            int limit,
            TimeSpan staleAfter,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task SetProcessingStatusAsync(
            DocsPublicDocument document,
            DocumentProcessingStatus status,
            CancellationToken ct
        ) => throw new NotSupportedException();
    }

    private sealed class TestPublicSourceStore : IDocsPublicSourceStore
    {
        public List<DocsPublicSource> Sources { get; } = [];

        public Task<DocsPublicSource?> GetByIdAsync(string id, CancellationToken ct) =>
            Task.FromResult(Sources.SingleOrDefault(s => s.Id == id));

        public Task<IReadOnlyList<DocsPublicSource>> GetByIdsAsync(
            IReadOnlyCollection<string> ids,
            CancellationToken ct
        ) =>
            Task.FromResult<IReadOnlyList<DocsPublicSource>>(
                Sources.Where(s => ids.Contains(s.Id)).ToArray()
            );

        public Task<DocsPublicSource?> GetByRepoUrlAsync(string repoUrl, CancellationToken ct) =>
            Task.FromResult(Sources.SingleOrDefault(s => s.RepoUrl == repoUrl));

        public Task<DocsPublicSource> CreateAsync(DocsPublicSource source, CancellationToken ct)
        {
            Sources.Add(source);
            return Task.FromResult(source);
        }

        public Task<DocsPublicSource> UpdateAsync(DocsPublicSource source, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<DocsPublicSource>> ClaimDueForSyncAsync(
            int limit,
            CancellationToken ct
        ) => throw new NotSupportedException();
    }

    private sealed class TestCodeRepositoryStore : ICodeRepositoryStore
    {
        public List<CodeRepository> Repositories { get; } = [];

        public Task<CodeRepository?> FindActiveForOrganizationAsync(
            string organizationId,
            string repositoryId,
            CancellationToken ct
        ) =>
            Task.FromResult(
                Repositories.SingleOrDefault(r =>
                    r.OrganizationId == organizationId && r.Id == repositoryId
                )
            );

        public Task<CodeRepository?> FindActiveAsync(
            string provider,
            string ownerQualifiedName,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<CodeRepository>> ListActiveForOrganizationAsync(
            string organizationId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<CodeRepository>> ListConfiguredForOrganizationAsync(
            string organizationId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<CodeRepository> UpsertAsync(
            CodeRepository repository,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<bool> DisableAsync(
            string organizationId,
            string repositoryId,
            DateTimeOffset disabledAtUtc,
            CancellationToken ct
        ) => throw new NotSupportedException();
    }

    private sealed class NotSupportedCodeRepositoryStore : ICodeRepositoryStore
    {
        public Task<CodeRepository?> FindActiveAsync(
            string provider,
            string ownerQualifiedName,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<CodeRepository>> ListActiveForOrganizationAsync(
            string organizationId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<CodeRepository>> ListConfiguredForOrganizationAsync(
            string organizationId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<CodeRepository?> FindActiveForOrganizationAsync(
            string organizationId,
            string repositoryId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<CodeRepository> UpsertAsync(
            CodeRepository repository,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<bool> DisableAsync(
            string organizationId,
            string repositoryId,
            DateTimeOffset disabledAtUtc,
            CancellationToken ct
        ) => throw new NotSupportedException();
    }
}
