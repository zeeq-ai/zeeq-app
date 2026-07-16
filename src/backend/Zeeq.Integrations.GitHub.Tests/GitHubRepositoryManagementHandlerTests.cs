using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using OpenIddict.Abstractions;
using Zeeq.Core.Documents;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Integrations.GitHub.Tests;

public sealed class GitHubRepositoryManagementHandlerTests
{
    [Test]
    public async Task CreateMapping_WithAvailableRepository_UpsertsOrganizationScopedMapping()
    {
        var store = new TestCodeRepositoryStore();
        var provider = new TestGitHubRepositoryProvider([
            new(
                GitHubRepositoryId: 123,
                NodeId: "repo_node",
                Name: "zeeq",
                OwnerQualifiedName: "zeeq-ai/zeeq",
                Private: true,
                DefaultBranch: "main",
                HtmlUrl: "https://github.com/zeeq-ai/zeeq"
            ),
        ]);
        var handler = new CreateGitHubRepositoryMappingHandler(
            store,
            provider,
            new TestLibraryDocumentStore()
        );

        var result = await handler.HandleAsync(
            new(OwnerQualifiedName: "zeeq-ai/ZEEQ", TeamId: "team_123", DisplayName: null),
            User(),
            CancellationToken.None
        );

        var created = (Created<GitHubConfiguredRepositoryResponse>)result;
        var saved = store.Repositories.Single();

        await Assert.That(created.Value!.OwnerQualifiedName).IsEqualTo("zeeq-ai/zeeq");
        await Assert.That(saved.OrganizationId).IsEqualTo("org_123");
        await Assert.That(saved.TeamId).IsEqualTo("team_123");
        await Assert.That(saved.Provider).IsEqualTo("github");
        await Assert.That(saved.DisplayName).IsEqualTo("zeeq-ai/zeeq");
        await Assert.That(saved.ReviewConfiguration.FileFilter.IncludedFiles).IsEmpty();
        await Assert.That(saved.ReviewConfiguration.FileFilter.ExcludedFiles).IsEmpty();
    }

    [Test]
    public async Task CreateMapping_WithRepositoryOutsideInstallation_DoesNotUpsert()
    {
        var store = new TestCodeRepositoryStore();
        var handler = new CreateGitHubRepositoryMappingHandler(
            store,
            new TestGitHubRepositoryProvider([]),
            new TestLibraryDocumentStore()
        );

        var result = await handler.HandleAsync(
            new("zeeq-ai/zeeq", TeamId: null, DisplayName: null),
            User(),
            CancellationToken.None
        );

        await Assert.That(result).IsTypeOf<NotFound<GitHubRepositoryManagementError>>();
        await Assert.That(store.Repositories).IsEmpty();
    }

    [Test]
    public async Task CreateMapping_WithoutInstallation_ReturnsNotFound()
    {
        var store = new TestCodeRepositoryStore();
        var handler = new CreateGitHubRepositoryMappingHandler(
            store,
            TestGitHubRepositoryProvider.WithoutInstallation(),
            new TestLibraryDocumentStore()
        );

        var result = await handler.HandleAsync(
            new("zeeq-ai/zeeq", TeamId: null, DisplayName: null),
            User(),
            CancellationToken.None
        );

        await Assert.That(result).IsTypeOf<NotFound<GitHubRepositoryManagementError>>();
        await Assert.That(store.Repositories).IsEmpty();
    }

    [Test]
    public async Task ListConfigured_WithPausedRepository_ReturnsRepository()
    {
        var configured = Repository("repo_configured", "zeeq-ai/zeeq");
        configured.Enabled = false;
        var store = new TestCodeRepositoryStore([configured]);
        var handler = new ListConfiguredGitHubRepositoriesHandler(store);

        var result = await handler.HandleAsync(User(), CancellationToken.None);

        var ok = (Ok<GitHubConfiguredRepositoryResponse[]>)result;
        var response = ok.Value!.Single();

        await Assert.That(response.Id).IsEqualTo(configured.Id);
        await Assert.That(response.Enabled).IsFalse();
    }

    [Test]
    public async Task ListConfigured_WithSoftDeletedRepository_DoesNotReturnRepository()
    {
        var configured = Repository("repo_configured", "zeeq-ai/zeeq");
        configured.Enabled = false;
        configured.DisabledAtUtc = DateTimeOffset.UtcNow;
        var store = new TestCodeRepositoryStore([configured]);
        var handler = new ListConfiguredGitHubRepositoriesHandler(store);

        var result = await handler.HandleAsync(User(), CancellationToken.None);

        var ok = (Ok<GitHubConfiguredRepositoryResponse[]>)result;

        await Assert.That(ok.Value).IsEmpty();
    }

    [Test]
    public async Task ListAvailable_WithConfiguredRepository_MarksConfiguredRepository()
    {
        var configured = Repository("repo_configured", "zeeq-ai/zeeq");
        var store = new TestCodeRepositoryStore([configured]);
        var provider = new TestGitHubRepositoryProvider([
            new(
                GitHubRepositoryId: 123,
                NodeId: "repo_node",
                Name: "zeeq",
                OwnerQualifiedName: "zeeq-ai/zeeq",
                Private: false,
                DefaultBranch: "main",
                HtmlUrl: "https://github.com/zeeq-ai/zeeq"
            ),
        ]);
        var handler = new ListAvailableGitHubRepositoriesHandler(store, provider);

        var result = await handler.HandleAsync(User(), CancellationToken.None);

        var ok = (Ok<IReadOnlyList<GitHubAvailableRepositoryResponse>>)result;
        var response = ok.Value!.Single();

        await Assert.That(response.Configured).IsTrue();
        await Assert.That(response.ConfiguredRepositoryId).IsEqualTo(configured.Id);
    }

    [Test]
    public async Task ListAvailable_WithPausedRepository_MarksRepositoryConfigured()
    {
        var configured = Repository("repo_configured", "zeeq-ai/zeeq");
        configured.Enabled = false;
        var store = new TestCodeRepositoryStore([configured]);
        var provider = new TestGitHubRepositoryProvider([
            new(
                GitHubRepositoryId: 123,
                NodeId: "repo_node",
                Name: "zeeq",
                OwnerQualifiedName: "zeeq-ai/zeeq",
                Private: false,
                DefaultBranch: "main",
                HtmlUrl: "https://github.com/zeeq-ai/zeeq"
            ),
        ]);
        var handler = new ListAvailableGitHubRepositoriesHandler(store, provider);

        var result = await handler.HandleAsync(User(), CancellationToken.None);

        var ok = (Ok<IReadOnlyList<GitHubAvailableRepositoryResponse>>)result;
        var response = ok.Value!.Single();

        await Assert.That(response.Configured).IsTrue();
        await Assert.That(response.ConfiguredRepositoryId).IsEqualTo(configured.Id);
    }

    [Test]
    public async Task ListAvailable_WithSoftDeletedRepository_DoesNotMarkRepositoryConfigured()
    {
        var configured = Repository("repo_configured", "zeeq-ai/zeeq");
        configured.Enabled = false;
        configured.DisabledAtUtc = DateTimeOffset.UtcNow;
        var store = new TestCodeRepositoryStore([configured]);
        var provider = new TestGitHubRepositoryProvider([
            new(
                GitHubRepositoryId: 123,
                NodeId: "repo_node",
                Name: "zeeq",
                OwnerQualifiedName: "zeeq-ai/zeeq",
                Private: false,
                DefaultBranch: "main",
                HtmlUrl: "https://github.com/zeeq-ai/zeeq"
            ),
        ]);
        var handler = new ListAvailableGitHubRepositoriesHandler(store, provider);

        var result = await handler.HandleAsync(User(), CancellationToken.None);

        var ok = (Ok<IReadOnlyList<GitHubAvailableRepositoryResponse>>)result;
        var response = ok.Value!.Single();

        await Assert.That(response.Configured).IsFalse();
        await Assert.That(response.ConfiguredRepositoryId).IsNull();
    }

    [Test]
    public async Task CreateMapping_WithSoftDeletedExistingRepository_CreatesNewActiveMapping()
    {
        var historical = Repository("repo_deleted", "zeeq-ai/zeeq");
        historical.Enabled = false;
        historical.DisabledAtUtc = DateTimeOffset.UtcNow;
        var store = new TestCodeRepositoryStore([historical]);
        var provider = new TestGitHubRepositoryProvider([
            new(
                GitHubRepositoryId: 123,
                NodeId: "repo_node",
                Name: "zeeq",
                OwnerQualifiedName: "zeeq-ai/zeeq",
                Private: false,
                DefaultBranch: "main",
                HtmlUrl: "https://github.com/zeeq-ai/zeeq"
            ),
        ]);
        var handler = new CreateGitHubRepositoryMappingHandler(
            store,
            provider,
            new TestLibraryDocumentStore()
        );

        var result = await handler.HandleAsync(
            new("zeeq-ai/zeeq", TeamId: null, DisplayName: null),
            User(),
            CancellationToken.None
        );

        var created = (Created<GitHubConfiguredRepositoryResponse>)result;
        var active = store.Repositories.Single(repository => repository.DisabledAtUtc is null);

        await Assert.That(created.Value!.Id).IsEqualTo(active.Id);
        await Assert.That(store.Repositories.Count).IsEqualTo(2);
    }

    [Test]
    public async Task UpdateMapping_WithExistingRepository_UpdatesLocalSettingsOnly()
    {
        var existing = Repository("repo_configured", "zeeq-ai/zeeq");
        var store = new TestCodeRepositoryStore([existing]);
        var handler = new UpdateGitHubRepositoryMappingHandler(
            store,
            new TestLibraryDocumentStore()
        );

        var result = await handler.HandleAsync(
            existing.Id,
            new(TeamId: null, DisplayName: "Primary repo", Enabled: false),
            User(),
            CancellationToken.None
        );

        var ok = (Ok<GitHubConfiguredRepositoryResponse>)result;
        var saved = store.Repositories.Single();

        await Assert.That(ok.Value!.DisplayName).IsEqualTo("Primary repo");
        await Assert.That(saved.OwnerQualifiedName).IsEqualTo("zeeq-ai/zeeq");
        await Assert.That(saved.Enabled).IsFalse();
    }

    [Test]
    public async Task DisableMapping_WithExistingRepository_DisablesOrganizationScopedMapping()
    {
        var existing = Repository("repo_configured", "zeeq-ai/zeeq");
        var store = new TestCodeRepositoryStore([existing]);
        var handler = new DisableGitHubRepositoryMappingHandler(store);

        var result = await handler.HandleAsync(existing.Id, User(), CancellationToken.None);

        await Assert.That(result).IsTypeOf<NoContent>();
        await Assert.That(store.Repositories.Single().DisabledAtUtc).IsNotNull();
        await Assert.That(store.Repositories.Single().Enabled).IsFalse();
    }

    [Test]
    public async Task CreateMapping_WithValidLibraryIds_SetsLibraryIdsOnCreatedRepository()
    {
        var store = new TestCodeRepositoryStore();
        var provider = new TestGitHubRepositoryProvider([
            new(
                GitHubRepositoryId: 123,
                NodeId: "repo_node",
                Name: "zeeq",
                OwnerQualifiedName: "zeeq-ai/zeeq",
                Private: true,
                DefaultBranch: "main",
                HtmlUrl: "https://github.com/zeeq-ai/zeeq"
            ),
        ]);
        var libraries = new TestLibraryDocumentStore([Library("lib_123", "kb")]);
        var handler = new CreateGitHubRepositoryMappingHandler(store, provider, libraries);

        var result = await handler.HandleAsync(
            new("zeeq-ai/zeeq", TeamId: null, DisplayName: null, LibraryIds: ["lib_123"]),
            User(),
            CancellationToken.None
        );

        await Assert.That(result).IsTypeOf<Created<GitHubConfiguredRepositoryResponse>>();
        await Assert.That(store.Repositories.Single().LibraryIds).Contains("lib_123");
    }

    [Test]
    public async Task CreateMapping_WithUnknownLibraryId_ReturnsBadRequest()
    {
        var store = new TestCodeRepositoryStore();
        var provider = new TestGitHubRepositoryProvider([
            new(
                GitHubRepositoryId: 123,
                NodeId: "repo_node",
                Name: "zeeq",
                OwnerQualifiedName: "zeeq-ai/zeeq",
                Private: true,
                DefaultBranch: "main",
                HtmlUrl: "https://github.com/zeeq-ai/zeeq"
            ),
        ]);
        var handler = new CreateGitHubRepositoryMappingHandler(
            store,
            provider,
            new TestLibraryDocumentStore()
        );

        var result = await handler.HandleAsync(
            new("zeeq-ai/zeeq", TeamId: null, DisplayName: null, LibraryIds: ["lib_unknown"]),
            User(),
            CancellationToken.None
        );

        await Assert.That(result).IsTypeOf<BadRequest<GitHubRepositoryManagementError>>();
        await Assert.That(store.Repositories).IsEmpty();
    }

    [Test]
    public async Task CreateMapping_WithNoLibraryIds_CreatesRepositoryWithEmptyArray()
    {
        var store = new TestCodeRepositoryStore();
        var provider = new TestGitHubRepositoryProvider([
            new(
                GitHubRepositoryId: 123,
                NodeId: "repo_node",
                Name: "zeeq",
                OwnerQualifiedName: "zeeq-ai/zeeq",
                Private: true,
                DefaultBranch: "main",
                HtmlUrl: "https://github.com/zeeq-ai/zeeq"
            ),
        ]);
        var handler = new CreateGitHubRepositoryMappingHandler(
            store,
            provider,
            new TestLibraryDocumentStore()
        );

        await handler.HandleAsync(
            new("zeeq-ai/zeeq", TeamId: null, DisplayName: null),
            User(),
            CancellationToken.None
        );

        await Assert.That(store.Repositories.Single().LibraryIds).IsEmpty();
    }

    [Test]
    public async Task UpdateMapping_WithNullLibraryIds_LeavesExistingLibraryIdsUnchanged()
    {
        var existing = Repository("repo_configured", "zeeq-ai/zeeq", libraryIds: ["lib_1"]);
        var store = new TestCodeRepositoryStore([existing]);
        var handler = new UpdateGitHubRepositoryMappingHandler(
            store,
            new TestLibraryDocumentStore()
        );

        await handler.HandleAsync(
            existing.Id,
            new(TeamId: null, DisplayName: null, LibraryIds: null),
            User(),
            CancellationToken.None
        );

        await Assert.That(store.Repositories.Single().LibraryIds).Contains("lib_1");
    }

    [Test]
    public async Task UpdateMapping_WithEmptyLibraryIds_ClearsMapping()
    {
        var existing = Repository("repo_configured", "zeeq-ai/zeeq", libraryIds: ["lib_1"]);
        var store = new TestCodeRepositoryStore([existing]);
        var handler = new UpdateGitHubRepositoryMappingHandler(
            store,
            new TestLibraryDocumentStore()
        );

        await handler.HandleAsync(
            existing.Id,
            new(TeamId: null, DisplayName: null, LibraryIds: []),
            User(),
            CancellationToken.None
        );

        await Assert.That(store.Repositories.Single().LibraryIds).IsEmpty();
    }

    [Test]
    public async Task UpdateMapping_WithUnknownLibraryId_ReturnsBadRequest()
    {
        var existing = Repository("repo_configured", "zeeq-ai/zeeq");
        var store = new TestCodeRepositoryStore([existing]);
        var handler = new UpdateGitHubRepositoryMappingHandler(
            store,
            new TestLibraryDocumentStore()
        );

        var result = await handler.HandleAsync(
            existing.Id,
            new(TeamId: null, DisplayName: null, LibraryIds: ["lib_unknown"]),
            User(),
            CancellationToken.None
        );

        await Assert.That(result).IsTypeOf<BadRequest<GitHubRepositoryManagementError>>();
    }

    [Test]
    public async Task ListConfigured_Response_IncludesLibraryIds()
    {
        var configured = Repository("repo_configured", "zeeq-ai/zeeq", libraryIds: ["lib_1"]);
        var store = new TestCodeRepositoryStore([configured]);
        var handler = new ListConfiguredGitHubRepositoriesHandler(store);

        var result = await handler.HandleAsync(User(), CancellationToken.None);

        var ok = (Ok<GitHubConfiguredRepositoryResponse[]>)result;
        await Assert.That(ok.Value!.Single().LibraryIds).Contains("lib_1");
    }

    private static ClaimsPrincipal User() =>
        new(
            new ClaimsIdentity(
                [
                    new(OpenIddictConstants.Claims.Subject, "usr_123"),
                    new(AuthClaims.OrganizationId, "org_123"),
                    new(AuthClaims.TeamId, "team_123"),
                ],
                authenticationType: "Test"
            )
        );

    private static CodeRepository Repository(
        string id,
        string ownerQualifiedName,
        string[]? libraryIds = null
    )
    {
        var now = DateTimeOffset.UtcNow;

        return new()
        {
            Id = id,
            OrganizationId = "org_123",
            TeamId = "team_123",
            Provider = "github",
            OwnerQualifiedName = ownerQualifiedName,
            DisplayName = ownerQualifiedName,
            Enabled = true,
            LibraryIds = libraryIds ?? [],
            ReviewConfiguration = CodeRepositoryReviewConfiguration.Empty,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
    }

    private static Library Library(string id, string name) =>
        new()
        {
            Id = id,
            OrganizationId = "org_123",
            Name = name,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };

    private sealed class TestGitHubRepositoryProvider(
        IReadOnlyList<GitHubAvailableRepository> repositories,
        bool installationAvailable = true
    ) : IGitHubRepositoryProvider
    {
        public static TestGitHubRepositoryProvider WithoutInstallation() => new([], false);

        public Task<IReadOnlyList<GitHubAvailableRepository>> ListAvailableAsync(
            string organizationId,
            CancellationToken cancellationToken
        ) =>
            installationAvailable
                ? Task.FromResult(repositories)
                : throw new GitHubInstallationUnavailableException(organizationId);
    }

    private sealed class TestCodeRepositoryStore(IReadOnlyList<CodeRepository>? seed = null)
        : ICodeRepositoryStore
    {
        public List<CodeRepository> Repositories { get; } = seed?.ToList() ?? [];

        public Task<CodeRepository?> FindActiveAsync(
            string provider,
            string ownerQualifiedName,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                Repositories.FirstOrDefault(repository =>
                    repository.Provider == provider
                    && repository.OwnerQualifiedName == ownerQualifiedName
                    && repository.DisabledAtUtc is null
                    && repository.Enabled
                )
            );

        public Task<IReadOnlyList<CodeRepository>> ListActiveForOrganizationAsync(
            string organizationId,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult<IReadOnlyList<CodeRepository>>([
                .. Repositories.Where(repository =>
                    repository.OrganizationId == organizationId
                    && repository.DisabledAtUtc is null
                    && repository.Enabled
                ),
            ]);

        public Task<IReadOnlyList<CodeRepository>> ListConfiguredForOrganizationAsync(
            string organizationId,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult<IReadOnlyList<CodeRepository>>([
                .. Repositories.Where(repository =>
                    repository.OrganizationId == organizationId && repository.DisabledAtUtc is null
                ),
            ]);

        public Task<CodeRepository?> FindActiveForOrganizationAsync(
            string organizationId,
            string repositoryId,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                Repositories.FirstOrDefault(repository =>
                    repository.OrganizationId == organizationId
                    && repository.Id == repositoryId
                    && repository.DisabledAtUtc is null
                )
            );

        public Task<CodeRepository> UpsertAsync(
            CodeRepository repository,
            CancellationToken cancellationToken
        )
        {
            var existing = Repositories.FirstOrDefault(row =>
                row.OrganizationId == repository.OrganizationId
                && row.Provider == repository.Provider
                && row.OwnerQualifiedName == repository.OwnerQualifiedName
                && row.DisabledAtUtc is null
            );

            if (existing is null)
            {
                Repositories.Add(repository);
                return Task.FromResult(repository);
            }

            existing.TeamId = repository.TeamId;
            existing.DisplayName = repository.DisplayName;
            existing.Enabled = repository.Enabled;
            existing.LibraryIds = repository.LibraryIds;
            existing.ReviewConfiguration = repository.ReviewConfiguration;
            existing.UpdatedAtUtc = repository.UpdatedAtUtc;

            return Task.FromResult(existing);
        }

        public Task<bool> DisableAsync(
            string organizationId,
            string repositoryId,
            DateTimeOffset disabledAtUtc,
            CancellationToken cancellationToken
        )
        {
            var existing = Repositories.FirstOrDefault(repository =>
                repository.OrganizationId == organizationId
                && repository.Id == repositoryId
                && repository.DisabledAtUtc is null
            );

            if (existing is null)
            {
                return Task.FromResult(false);
            }

            existing.Enabled = false;
            existing.DisabledAtUtc = disabledAtUtc;
            existing.UpdatedAtUtc = disabledAtUtc;

            return Task.FromResult(true);
        }
    }

    private sealed class TestLibraryDocumentStore(IReadOnlyList<Library>? seed = null)
        : ILibraryDocumentStore
    {
        private readonly List<Library> _libraries = seed?.ToList() ?? [];

        public Task<IReadOnlyList<Library>> ListLibrariesAsync(
            string organizationId,
            CancellationToken ct
        ) =>
            Task.FromResult<IReadOnlyList<Library>>(
                _libraries.Where(l => l.OrganizationId == organizationId).ToArray()
            );

        public Task<IReadOnlyList<Library>> ListLibrariesByPublicSourceIdAsync(
            string publicSourceId,
            CancellationToken ct
        ) =>
            Task.FromResult<IReadOnlyList<Library>>(
                _libraries.Where(l => l.PublicSourceId == publicSourceId).ToArray()
            );

        public Task<Library?> GetLibraryAsync(
            string organizationId,
            string name,
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
}
