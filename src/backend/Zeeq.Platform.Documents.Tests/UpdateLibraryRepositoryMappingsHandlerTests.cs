using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Zeeq.Core.Documents;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Platform.Documents.Tests;

public sealed class UpdateLibraryRepositoryMappingsHandlerTests
{
    [Test]
    public async Task HandleAsync_NoActiveOrganization_ReturnsBadRequest()
    {
        var handler = new UpdateLibraryRepositoryMappingsHandler(
            new TestLibraryDocumentStore(),
            new TestCodeRepositoryStore()
        );

        var result = await handler.HandleAsync(
            "",
            "kb",
            new UpdateLibraryRepositoryMappingsRequest { RepositoryIds = [] },
            UserWithoutOrg(),
            CancellationToken.None
        );

        await Assert.That(result.Result).IsTypeOf<BadRequest<LibraryError>>();
    }

    [Test]
    public async Task HandleAsync_LibraryNotFound_ReturnsNotFound()
    {
        var handler = new UpdateLibraryRepositoryMappingsHandler(
            new TestLibraryDocumentStore(),
            new TestCodeRepositoryStore()
        );

        var result = await handler.HandleAsync(
            "org_123",
            "nonexistent",
            new UpdateLibraryRepositoryMappingsRequest { RepositoryIds = [] },
            User(),
            CancellationToken.None
        );

        await Assert.That(result.Result).IsTypeOf<NotFound>();
    }

    [Test]
    public async Task HandleAsync_UnknownRepositoryId_ReturnsBadRequestNamingTheId()
    {
        var libraryStore = new TestLibraryDocumentStore([Library("lib_1", "kb")]);
        var repoStore = new TestCodeRepositoryStore([Repo("repo_A")]);
        var handler = new UpdateLibraryRepositoryMappingsHandler(libraryStore, repoStore);

        var result = await handler.HandleAsync(
            "org_123",
            "kb",
            new UpdateLibraryRepositoryMappingsRequest { RepositoryIds = ["repo_UNKNOWN"] },
            User(),
            CancellationToken.None
        );

        var badRequest = result.Result as BadRequest<LibraryError>;
        await Assert.That(badRequest).IsNotNull();
        await Assert.That(badRequest!.Value!.Message).Contains("repo_UNKNOWN");
    }

    [Test]
    public async Task HandleAsync_AddsLibraryToNewlyRequestedRepository_UpsertsThatRepositoryOnly()
    {
        var library = Library("lib_1", "kb");
        var repoA = Repo("repo_A");
        var repoB = Repo("repo_B");
        var repoStore = new TestCodeRepositoryStore([repoA, repoB]);
        var handler = new UpdateLibraryRepositoryMappingsHandler(
            new TestLibraryDocumentStore([library]),
            repoStore
        );

        await handler.HandleAsync(
            "org_123",
            "kb",
            new UpdateLibraryRepositoryMappingsRequest { RepositoryIds = ["repo_A"] },
            User(),
            CancellationToken.None
        );

        await Assert.That(repoStore.UpsertedIds).HasSingleItem();
        await Assert.That(repoStore.UpsertedIds).Contains("repo_A");
        await Assert.That(repoA.LibraryIds).Contains("lib_1");
        await Assert.That(repoB.LibraryIds).IsEmpty();
    }

    [Test]
    public async Task HandleAsync_RemovesLibraryFromNoLongerRequestedRepository_UpsertsThatRepositoryOnly()
    {
        var library = Library("lib_1", "kb");
        var repoA = Repo("repo_A", libraryIds: ["lib_1"]);
        var repoB = Repo("repo_B");
        var repoStore = new TestCodeRepositoryStore([repoA, repoB]);
        var handler = new UpdateLibraryRepositoryMappingsHandler(
            new TestLibraryDocumentStore([library]),
            repoStore
        );

        await handler.HandleAsync(
            "org_123",
            "kb",
            new UpdateLibraryRepositoryMappingsRequest { RepositoryIds = [] },
            User(),
            CancellationToken.None
        );

        await Assert.That(repoStore.UpsertedIds).HasSingleItem();
        await Assert.That(repoStore.UpsertedIds).Contains("repo_A");
        await Assert.That(repoA.LibraryIds).IsEmpty();
    }

    [Test]
    public async Task HandleAsync_UnchangedRepositories_AreNotUpserted()
    {
        var library = Library("lib_1", "kb");
        // repoA already has the library — in request → no change
        var repoA = Repo("repo_A", libraryIds: ["lib_1"]);
        // repoB has no library — not in request → no change
        var repoB = Repo("repo_B");
        var repoStore = new TestCodeRepositoryStore([repoA, repoB]);
        var handler = new UpdateLibraryRepositoryMappingsHandler(
            new TestLibraryDocumentStore([library]),
            repoStore
        );

        await handler.HandleAsync(
            "org_123",
            "kb",
            new UpdateLibraryRepositoryMappingsRequest { RepositoryIds = ["repo_A"] },
            User(),
            CancellationToken.None
        );

        await Assert.That(repoStore.UpsertedIds).IsEmpty();
    }

    [Test]
    public async Task HandleAsync_EmptyRequestedSet_ClearsAllMappingsForThisLibrary()
    {
        var library = Library("lib_1", "kb");
        var repoA = Repo("repo_A", libraryIds: ["lib_1"]);
        var repoB = Repo("repo_B", libraryIds: ["lib_1"]);
        var repoStore = new TestCodeRepositoryStore([repoA, repoB]);
        var handler = new UpdateLibraryRepositoryMappingsHandler(
            new TestLibraryDocumentStore([library]),
            repoStore
        );

        await handler.HandleAsync(
            "org_123",
            "kb",
            new UpdateLibraryRepositoryMappingsRequest { RepositoryIds = [] },
            User(),
            CancellationToken.None
        );

        await Assert.That(repoStore.UpsertedIds).Count().IsEqualTo(2);
        await Assert.That(repoA.LibraryIds).IsEmpty();
        await Assert.That(repoB.LibraryIds).IsEmpty();
    }

    [Test]
    public async Task HandleAsync_RepositoryFromAnotherOrganization_IsNeverConsidered()
    {
        var library = Library("lib_1", "kb");
        var orgRepo = Repo("repo_A");
        var otherOrgRepo = Repo("repo_X", orgId: "org_OTHER");
        var repoStore = new TestCodeRepositoryStore([orgRepo, otherOrgRepo]);
        var handler = new UpdateLibraryRepositoryMappingsHandler(
            new TestLibraryDocumentStore([library]),
            repoStore
        );

        // repo_X is invisible because ListConfiguredForOrganizationAsync filters by org.
        var result = await handler.HandleAsync(
            "org_123",
            "kb",
            new UpdateLibraryRepositoryMappingsRequest { RepositoryIds = ["repo_A"] },
            User(),
            CancellationToken.None
        );

        await Assert.That(result.Result).IsTypeOf<Ok<LibraryRepositoryMappingsResponse>>();
        await Assert.That(repoStore.UpsertedIds).Contains("repo_A");
        await Assert.That(otherOrgRepo.LibraryIds).IsEmpty();
    }

    private static ClaimsPrincipal User() =>
        new(
            new ClaimsIdentity(
                [new Claim(AuthClaims.OrganizationId, "org_123")],
                authenticationType: "test"
            )
        );

    private static ClaimsPrincipal UserWithoutOrg() =>
        new(new ClaimsIdentity([], authenticationType: "test"));

    private static Library Library(string id, string name) =>
        new()
        {
            Id = id,
            OrganizationId = "org_123",
            Name = name,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };

    private static CodeRepository Repo(string id, string[]? libraryIds = null, string? orgId = null)
    {
        var now = DateTimeOffset.UtcNow;

        return new()
        {
            Id = id,
            OrganizationId = orgId ?? "org_123",
            Provider = "github",
            OwnerQualifiedName = $"org/{id}",
            DisplayName = id,
            Enabled = true,
            LibraryIds = libraryIds ?? [],
            ReviewConfiguration = CodeRepositoryReviewConfiguration.Empty,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
    }

    private sealed class TestLibraryDocumentStore(IReadOnlyList<Library>? seed = null)
        : ILibraryDocumentStore
    {
        private readonly List<Library> _libraries = seed?.ToList() ?? [];

        public Task<Library?> GetLibraryAsync(
            string organizationId,
            string name,
            CancellationToken ct
        ) =>
            Task.FromResult(
                _libraries.FirstOrDefault(l => l.OrganizationId == organizationId && l.Name == name)
            );

        public Task<IReadOnlyList<Library>> ListLibrariesAsync(
            string organizationId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<Library>> ListLibrariesByPublicSourceIdAsync(
            string publicSourceId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<Library> CreateLibraryAsync(Library library, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Library> UpdateLibraryAsync(Library library, CancellationToken ct) =>
            throw new NotSupportedException();

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
        ) => throw new NotSupportedException();

        public Task DeleteDocumentAsync(
            string organizationId,
            string libraryId,
            string path,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<LibraryDocument?> GetByPathAsync(
            string organizationId,
            string libraryId,
            string input,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<LibraryDocumentMatch>> SearchAsync(
            string organizationId,
            string libraryId,
            string query,
            int limit,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<LibraryDocument>> ListDocumentsAsync(
            string organizationId,
            string libraryId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<LibraryDocument?> MoveDocumentAsync(
            string organizationId,
            string libraryId,
            string fromPath,
            string toPath,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<LibraryDocument?> SetCodeReviewExclusionAsync(
            string organizationId,
            string libraryId,
            string path,
            bool excluded,
            CancellationToken ct
        ) => throw new NotSupportedException();
    }

    private sealed class TestCodeRepositoryStore(IReadOnlyList<CodeRepository>? seed = null)
        : ICodeRepositoryStore
    {
        private readonly List<CodeRepository> _repos = seed?.ToList() ?? [];
        public List<string> UpsertedIds { get; } = [];

        public Task<IReadOnlyList<CodeRepository>> ListConfiguredForOrganizationAsync(
            string organizationId,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult<IReadOnlyList<CodeRepository>>(
                _repos
                    .Where(r => r.OrganizationId == organizationId && r.DisabledAtUtc is null)
                    .ToArray()
            );

        public Task<CodeRepository> UpsertAsync(
            CodeRepository repository,
            CancellationToken cancellationToken
        )
        {
            UpsertedIds.Add(repository.Id);
            var existing = _repos.FirstOrDefault(r => r.Id == repository.Id);
            if (existing is not null)
            {
                existing.LibraryIds = repository.LibraryIds;
                existing.UpdatedAtUtc = repository.UpdatedAtUtc;
            }
            else
            {
                _repos.Add(repository);
            }

            return Task.FromResult(repository);
        }

        public Task<CodeRepository?> FindActiveAsync(
            string provider,
            string ownerQualifiedName,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<CodeRepository?> FindActiveForOrganizationByProviderIdentityAsync(
            string organizationId,
            string provider,
            string ownerQualifiedName,
            CancellationToken ct
        ) =>
            Task.FromResult(
                _repos.FirstOrDefault(r =>
                    r.OrganizationId == organizationId
                    && r.Provider == provider
                    && r.OwnerQualifiedName == ownerQualifiedName
                    && r.DisabledAtUtc is null
                    && r.Enabled
                )
            );

        public Task<IReadOnlyList<CodeRepository>> ListActiveForOrganizationAsync(
            string organizationId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<CodeRepository?> FindActiveForOrganizationAsync(
            string organizationId,
            string repositoryId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<bool> DisableAsync(
            string organizationId,
            string repositoryId,
            DateTimeOffset disabledAtUtc,
            CancellationToken ct
        ) => throw new NotSupportedException();
    }
}
