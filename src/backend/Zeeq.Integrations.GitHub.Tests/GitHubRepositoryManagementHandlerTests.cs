using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using OpenIddict.Abstractions;
using Zeeq.Core.Documents;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Integrations.GitHub.Tests;

public sealed class GitHubRepositoryManagementHandlerTests
{
    [Test]
    public async Task MapEndpoints_ListConfiguredRequiresCookieAuthButNoOwnerAdminRole()
    {
        var endpoint = await MapEndpointByNameAsync("ListConfiguredGitHubRepositories");
        var authorization = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();

        await Assert
            .That(
                authorization.Any(data =>
                    data.AuthenticationSchemes == SetupIdentityExtension.CookieScheme
                )
            )
            .IsTrue();
        await Assert
            .That(authorization.Any(data => !string.IsNullOrWhiteSpace(data.Roles)))
            .IsFalse();
        await Assert.That(AuthorizationPolicyRoles(endpoint).Count).IsEqualTo(0);
    }

    [Test]
    public async Task MapEndpoints_RepositoryManagementRoutesRequireOwnerOrAdmin()
    {
        var managementEndpointNames = new[]
        {
            "ListAvailableGitHubRepositories",
            "CreateGitHubRepositoryMapping",
            "UpdateGitHubRepositoryVisibility",
            "UpdateGitHubRepositoryMapping",
            "DisableGitHubRepositoryMapping",
        };

        foreach (var endpointName in managementEndpointNames)
        {
            var endpoint = await MapEndpointByNameAsync(endpointName);
            var authorization = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();

            await Assert
                .That(
                    authorization.Any(data =>
                        data.AuthenticationSchemes == SetupIdentityExtension.CookieScheme
                    )
                )
                .IsTrue();
            await Assert
                .That(AuthorizationPolicyRoles(endpoint).SetEquals(["owner", "admin"]))
                .IsTrue();
        }
    }

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
        await Assert.That(response.VisibleInLibraryPicker).IsTrue();
    }

    [Test]
    public async Task ListAvailable_WithUnconfiguredRepository_DefaultsVisibleInLibraryPicker()
    {
        var store = new TestCodeRepositoryStore();
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
        await Assert.That(response.VisibleInLibraryPicker).IsTrue();
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
        await Assert.That(response.VisibleInLibraryPicker).IsTrue();
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
        await Assert.That(saved.VisibleInLibraryPicker).IsTrue();
    }

    [Test]
    public async Task UpdateVisibility_WithUnconfiguredRepository_CreatesDisabledVisibilityRow()
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
        var handler = new UpdateGitHubRepositoryVisibilityHandler(store, provider);

        var result = await handler.HandleAsync(
            new("zeeq-ai/zeeq", VisibleInLibraryPicker: false),
            User(),
            CancellationToken.None
        );

        var ok = (Ok<GitHubConfiguredRepositoryResponse>)result;
        var saved = store.Repositories.Single();

        await Assert.That(ok.Value!.VisibleInLibraryPicker).IsFalse();
        await Assert.That(saved.Enabled).IsFalse();
        await Assert.That(saved.VisibleInLibraryPicker).IsFalse();
    }

    [Test]
    public async Task UpdateVisibility_WithEnabledRepository_DoesNotChangeEnabled()
    {
        var existing = Repository("repo_configured", "zeeq-ai/zeeq");
        existing.Enabled = true;
        var store = new TestCodeRepositoryStore([existing]);
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
        var handler = new UpdateGitHubRepositoryVisibilityHandler(store, provider);

        await handler.HandleAsync(
            new("zeeq-ai/zeeq", VisibleInLibraryPicker: false),
            User(),
            CancellationToken.None
        );

        var saved = store.Repositories.Single();

        await Assert.That(saved.Enabled).IsTrue();
        await Assert.That(saved.VisibleInLibraryPicker).IsFalse();
    }

    [Test]
    public async Task UpdateVisibility_WithRepositoryOutsideInstallation_DoesNotUpsert()
    {
        var store = new TestCodeRepositoryStore();
        var handler = new UpdateGitHubRepositoryVisibilityHandler(
            store,
            new TestGitHubRepositoryProvider([])
        );

        var result = await handler.HandleAsync(
            new("zeeq-ai/zeeq", VisibleInLibraryPicker: false),
            User(),
            CancellationToken.None
        );

        await Assert.That(result).IsTypeOf<NotFound<GitHubRepositoryManagementError>>();
        await Assert.That(store.Repositories).IsEmpty();
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
        await Assert.That(ok.Value!.Single().VisibleInLibraryPicker).IsTrue();
    }

    [Test]
    public async Task MapEndpoints_LibraryAndPromptRoutesRequireCookieAuthButNoOwnerAdminRole()
    {
        // These four routes are the member-accessible half of this endpoint group. If one ever drifts
        // onto the management group it would silently become admin-only and the repository view would
        // break for ordinary members; if the reverse happened, a member could reach privileged
        // repository settings. Pin the intent rather than relying on where the code happens to sit.
        var memberAccessibleEndpointNames = new[]
        {
            "UpdateRepositoryLibraries",
            "ListRepositoryPrompts",
            "GetRepositoryPrompt",
            "SaveRepositoryPrompt",
        };

        foreach (var endpointName in memberAccessibleEndpointNames)
        {
            var endpoint = await MapEndpointByNameAsync(endpointName);
            var authorization = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();

            await Assert
                .That(
                    authorization.Any(data =>
                        data.AuthenticationSchemes == SetupIdentityExtension.CookieScheme
                    )
                )
                .IsTrue();
            await Assert
                .That(authorization.Any(data => !string.IsNullOrWhiteSpace(data.Roles)))
                .IsFalse();
            await Assert.That(AuthorizationPolicyRoles(endpoint).Count).IsEqualTo(0);
        }
    }

    // ── Repository library mapping (member-accessible) ──────────────────────

    [Test]
    public async Task UpdateRepositoryLibraries_ReplacesLibrariesAndLeavesPrivilegedFieldsUntouched()
    {
        // This is the invariant that makes the looser authorization safe: the endpoint is reachable
        // by any organization member, so it must be structurally incapable of flipping Enabled,
        // DisplayName, or TeamId — the fields the admin-gated update endpoint owns.
        var repository = Repository(libraryIds: ["lib_1"]);
        repository.Enabled = false;
        repository.DisplayName = "Original name";
        repository.TeamId = "team_original";

        var repositories = new TestCodeRepositoryStore([repository]);
        var libraries = new TestLibraryDocumentStore([Library("lib_2", "Runbooks")]);
        var handler = new UpdateRepositoryLibrariesHandler(repositories, libraries);

        var result = await handler.HandleAsync(
            repository.Id,
            new UpdateRepositoryLibrariesRequest(["lib_2"]),
            User(),
            CancellationToken.None
        );

        await Assert.That(result).IsTypeOf<Ok<GitHubConfiguredRepositoryResponse>>();
        var saved = repositories.Repositories.Single();
        await Assert.That(saved.LibraryIds).IsEquivalentTo(["lib_2"]);
        await Assert.That(saved.Enabled).IsFalse();
        await Assert.That(saved.DisplayName).IsEqualTo("Original name");
        await Assert.That(saved.TeamId).IsEqualTo("team_original");
    }

    [Test]
    public async Task UpdateRepositoryLibraries_UnknownRepository_ReturnsNotFound()
    {
        var handler = new UpdateRepositoryLibrariesHandler(
            new TestCodeRepositoryStore(),
            new TestLibraryDocumentStore()
        );

        var result = await handler.HandleAsync(
            "repo_missing",
            new UpdateRepositoryLibrariesRequest([]),
            User(),
            CancellationToken.None
        );

        await Assert.That(result).IsTypeOf<NotFound<GitHubRepositoryManagementError>>();
    }

    // ── Repository prompt configuration ─────────────────────────────────────

    [Test]
    public async Task ListRepositoryPrompts_MergesActivationStateOntoOrganizationPrompts()
    {
        var repository = Repository();
        var documents = new TestLibraryDocumentStore();
        documents.ScopedSkillDocuments.Add(Prompt("document_configured", "Configured"));
        documents.ScopedSkillDocuments.Add(Prompt("document_untouched", "Untouched"));

        var configurations = new TestPromptConfigurationStore();
        configurations.Rows.Add(
            Configuration(repository.Id, "document_configured", active: true, ("rules", "Custom"))
        );

        var handler = new ListRepositoryPromptsHandler(
            new TestCodeRepositoryStore([repository]),
            configurations,
            documents
        );

        var result = await handler.HandleAsync(repository.Id, User(), CancellationToken.None);

        var ok = (Ok<RepositoryPromptSummaryResponse[]>)result;
        var configured = ok.Value!.Single(prompt => prompt.DocumentId == "document_configured");
        var untouched = ok.Value!.Single(prompt => prompt.DocumentId == "document_untouched");

        await Assert.That(configured.Active).IsTrue();
        await Assert.That(configured.ConfiguredValueCount).IsEqualTo(1);
        // A prompt the repository never configured still appears, so the catalog is complete.
        await Assert.That(untouched.Active).IsFalse();
        await Assert.That(untouched.ConfiguredValueCount).IsEqualTo(0);
    }

    [Test]
    public async Task ListRepositoryPrompts_PausedRepository_StillReturnsPromptCatalog()
    {
        var repository = Repository();
        repository.Enabled = false;
        var documents = new TestLibraryDocumentStore();
        documents.ScopedSkillDocuments.Add(Prompt("document_1", "Workflow"));

        var handler = new ListRepositoryPromptsHandler(
            new TestCodeRepositoryStore([repository]),
            new TestPromptConfigurationStore(),
            documents
        );

        var result = await handler.HandleAsync(repository.Id, User(), CancellationToken.None);

        var ok = (Ok<RepositoryPromptSummaryResponse[]>)result;
        await Assert.That(ok.Value!).Count().IsEqualTo(1);
    }

    [Test]
    public async Task ListRepositoryPrompts_UsesOrganizationLibraryDocumentKey()
    {
        var repository = Repository();
        var documents = new TestLibraryDocumentStore();
        documents.ScopedSkillDocuments.Add(
            Prompt("document_shared", "Runbook", libraryId: "lib_1")
        );
        documents.ScopedSkillDocuments.Add(
            Prompt("document_shared", "Playbook", libraryId: "lib_2")
        );

        var configurations = new TestPromptConfigurationStore();
        configurations.Rows.Add(
            ConfigurationForLibrary(
                repository.Id,
                "document_shared",
                libraryId: "lib_2",
                active: true,
                ("rules", "Custom")
            )
        );

        var handler = new ListRepositoryPromptsHandler(
            new TestCodeRepositoryStore([repository]),
            configurations,
            documents
        );

        var result = await handler.HandleAsync(repository.Id, User(), CancellationToken.None);

        var ok = (Ok<RepositoryPromptSummaryResponse[]>)result;
        var libOne = ok.Value!.Single(prompt => prompt.LibraryId == "lib_1");
        var libTwo = ok.Value!.Single(prompt => prompt.LibraryId == "lib_2");

        await Assert.That(libOne.Active).IsFalse();
        await Assert.That(libOne.ConfiguredValueCount).IsEqualTo(0);
        await Assert.That(libTwo.Active).IsTrue();
        await Assert.That(libTwo.ConfiguredValueCount).IsEqualTo(1);
    }

    [Test]
    public async Task GetRepositoryPrompt_MergesSavedValuesOntoDeclaredPlaceholders()
    {
        var repository = Repository();
        var documents = new TestLibraryDocumentStore();
        documents.ScopedSkillDocuments.Add(
            Prompt(
                "document_1",
                "Workflow",
                """
                <zeeq_placeholder name="rules" label="Rules">Default rules</zeeq_placeholder>
                <zeeq_placeholder name="untouched">Default untouched</zeeq_placeholder>
                """
            )
        );

        var configurations = new TestPromptConfigurationStore();
        configurations.Rows.Add(
            Configuration(repository.Id, "document_1", active: true, ("rules", "Overridden"))
        );

        var handler = new GetRepositoryPromptHandler(
            new TestCodeRepositoryStore([repository]),
            configurations,
            documents
        );

        var result = await handler.HandleAsync(
            repository.Id,
            "document_1",
            "lib_1",
            User(),
            CancellationToken.None
        );

        var ok = (Ok<RepositoryPromptDetailResponse>)result;
        var overridden = ok.Value!.Placeholders.Single(p => p.Name == "rules");
        var untouched = ok.Value!.Placeholders.Single(p => p.Name == "untouched");

        await Assert.That(ok.Value!.Active).IsTrue();
        await Assert.That(overridden.Value).IsEqualTo("Overridden");
        await Assert.That(overridden.DefaultValue).IsEqualTo("Default rules");
        await Assert.That(overridden.Label).IsEqualTo("Rules");
        // Null rather than empty string: the UI must distinguish "not customized" from "render nothing".
        await Assert.That(untouched.Value).IsNull();
        await Assert.That(untouched.DefaultValue).IsEqualTo("Default untouched");
    }

    [Test]
    public async Task GetRepositoryPrompt_DeactivatedPrompt_StillReturnsSavedValues()
    {
        // Deactivating must not look like it discarded the user's input, or reactivating would be
        // a destructive surprise.
        var repository = Repository();
        var documents = new TestLibraryDocumentStore();
        documents.ScopedSkillDocuments.Add(
            Prompt(
                "document_1",
                "Workflow",
                """<zeeq_placeholder name="rules">Default rules</zeeq_placeholder>"""
            )
        );

        var configurations = new TestPromptConfigurationStore();
        configurations.Rows.Add(
            Configuration(repository.Id, "document_1", active: false, ("rules", "Kept"))
        );

        var handler = new GetRepositoryPromptHandler(
            new TestCodeRepositoryStore([repository]),
            configurations,
            documents
        );

        var result = await handler.HandleAsync(
            repository.Id,
            "document_1",
            "lib_1",
            User(),
            CancellationToken.None
        );

        var ok = (Ok<RepositoryPromptDetailResponse>)result;
        await Assert.That(ok.Value!.Active).IsFalse();
        await Assert.That(ok.Value!.Placeholders.Single().Value).IsEqualTo("Kept");
    }

    [Test]
    public async Task SaveRepositoryPrompt_PersistsActivationAndValues()
    {
        var repository = Repository();
        var documents = new TestLibraryDocumentStore();
        documents.ScopedSkillDocuments.Add(
            Prompt(
                "document_1",
                "Workflow",
                """<zeeq_placeholder name="rules">Default rules</zeeq_placeholder>"""
            )
        );

        var configurations = new TestPromptConfigurationStore();
        var handler = new SaveRepositoryPromptHandler(
            new TestCodeRepositoryStore([repository]),
            configurations,
            documents
        );

        var result = await handler.HandleAsync(
            repository.Id,
            "document_1",
            new SaveRepositoryPromptRequest(
                "lib_1",
                Active: true,
                Values: new() { ["rules"] = "Repository rules" }
            ),
            User(),
            CancellationToken.None
        );

        var ok = (Ok<RepositoryPromptDetailResponse>)result;
        await Assert.That(ok.Value!.Placeholders.Single().Value).IsEqualTo("Repository rules");

        var saved = configurations.Rows.Single();
        await Assert.That(saved.Active).IsTrue();
        await Assert.That(saved.PlaceholderValues["rules"]).IsEqualTo("Repository rules");
        // The substitution path probes this dictionary with a span lookup, which needs an ordinal
        // comparer; building it correctly at the boundary avoids a rebuild on every prompt fetch.
        await Assert
            .That(saved.PlaceholderValues.TryGetAlternateLookup<ReadOnlySpan<char>>(out _))
            .IsTrue();
    }

    [Test]
    public async Task SaveRepositoryPrompt_OversizedValue_ReturnsBadRequest()
    {
        var repository = Repository();
        var handler = new SaveRepositoryPromptHandler(
            new TestCodeRepositoryStore([repository]),
            new TestPromptConfigurationStore(),
            new TestLibraryDocumentStore()
        );

        var result = await handler.HandleAsync(
            repository.Id,
            "document_1",
            new SaveRepositoryPromptRequest(
                "lib_1",
                Active: true,
                Values: new() { ["rules"] = new string('x', 8_001) }
            ),
            User(),
            CancellationToken.None
        );

        await Assert.That(result).IsTypeOf<BadRequest<GitHubRepositoryManagementError>>();
    }

    [Test]
    public async Task SaveRepositoryPrompt_NullValue_ReturnsBadRequest()
    {
        var repository = Repository();
        var handler = new SaveRepositoryPromptHandler(
            new TestCodeRepositoryStore([repository]),
            new TestPromptConfigurationStore(),
            new TestLibraryDocumentStore()
        );

        var result = await handler.HandleAsync(
            repository.Id,
            "document_1",
            new SaveRepositoryPromptRequest(
                "lib_1",
                Active: true,
                Values: new() { ["rules"] = null! }
            ),
            User(),
            CancellationToken.None
        );

        await Assert.That(result).IsTypeOf<BadRequest<GitHubRepositoryManagementError>>();
    }

    [Test]
    public async Task SaveRepositoryPromptRequest_WhitespaceLibraryId_FailsValidation()
    {
        var request = new SaveRepositoryPromptRequest("   ", Active: true);
        var results = new List<ValidationResult>();

        var valid = Validator.TryValidateObject(
            request,
            new ValidationContext(request),
            results,
            validateAllProperties: true
        );

        await Assert.That(valid).IsFalse();
    }

    [Test]
    public async Task SaveRepositoryPrompt_DocumentThatIsNotAnOrganizationPrompt_ReturnsNotFound()
    {
        // Without this guard a caller could seed configuration rows against arbitrary document ids.
        var repository = Repository();
        var handler = new SaveRepositoryPromptHandler(
            new TestCodeRepositoryStore([repository]),
            new TestPromptConfigurationStore(),
            new TestLibraryDocumentStore()
        );

        var result = await handler.HandleAsync(
            repository.Id,
            "document_not_a_prompt",
            new SaveRepositoryPromptRequest("lib_1", Active: true),
            User(),
            CancellationToken.None
        );

        await Assert.That(result).IsTypeOf<NotFound<GitHubRepositoryManagementError>>();
    }

    private static CodeRepository Repository(string[]? libraryIds = null) =>
        new()
        {
            Id = "repo_1",
            OrganizationId = "org_123",
            Provider = "github",
            OwnerQualifiedName = "acme/widgets",
            DisplayName = "acme/widgets",
            Enabled = true,
            LibraryIds = libraryIds ?? [],
        };

    private static LibraryScopedSkillDocument Prompt(
        string documentId,
        string title,
        string content = "Plain prompt body.",
        string libraryId = "lib_1"
    ) =>
        new(
            OrganizationId: "org_123",
            LibraryId: libraryId,
            LibraryName: "Handbook",
            DocumentId: documentId,
            Path: $"/prompts/{documentId}.md",
            Title: title,
            ManualSkillName: null,
            ParsedSkillName: null,
            ManualSkillDescription: null,
            ParsedSkillDescription: null,
            Metadata: null,
            Content: content,
            UpdatedAt: DateTimeOffset.UnixEpoch
        );

    private static CodeRepositoryPromptConfiguration Configuration(
        string repositoryId,
        string documentId,
        bool active,
        params (string Name, string Value)[] values
    ) =>
        ConfigurationForLibrary(
            repositoryId,
            documentId,
            libraryId: "lib_1",
            active: active,
            values: values
        );

    private static CodeRepositoryPromptConfiguration ConfigurationForLibrary(
        string repositoryId,
        string documentId,
        string libraryId,
        bool active,
        params (string Name, string Value)[] values
    )
    {
        var placeholderValues = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in values)
        {
            placeholderValues[name] = value;
        }

        return new()
        {
            Id = $"rpc_{documentId}",
            OrganizationId = "org_123",
            RepositoryId = repositoryId,
            LibraryId = libraryId,
            DocumentId = documentId,
            Active = active,
            PlaceholderValues = placeholderValues,
        };
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

    private static async Task<RouteEndpoint> MapEndpointByNameAsync(string endpointName)
    {
        var builder = WebApplication.CreateBuilder();
        await using var app = builder.Build();
        var endpoints = new GitHubRepositoryEndpoints();

        endpoints.MapEndpoints(app, app);

        return ((IEndpointRouteBuilder)app)
            .DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(endpoint =>
                endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == endpointName
            );
    }

    private static HashSet<string> AuthorizationPolicyRoles(RouteEndpoint endpoint) =>
        endpoint
            .Metadata.GetOrderedMetadata<AuthorizationPolicy>()
            .SelectMany(policy => policy.Requirements.OfType<RolesAuthorizationRequirement>())
            .SelectMany(requirement => requirement.AllowedRoles)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

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

        // The prompt-token lookup is exercised by the MCP dynamic-prompt tests, not this fixture.

        public Task<CodeRepository?> FindConfiguredForOrganizationByProviderIdentityAsync(
            string organizationId,
            string provider,
            string ownerQualifiedName,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<CodeRepository?> FindActiveForOrganizationByProviderIdentityAsync(
            string organizationId,
            string provider,
            string ownerQualifiedName,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                Repositories.FirstOrDefault(repository =>
                    repository.OrganizationId == organizationId
                    && repository.Provider == provider
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
            existing.VisibleInLibraryPicker = repository.VisibleInLibraryPicker;
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

        /// <summary>
        /// Organization-scoped prompt documents surfaced to the repository prompt endpoints.
        /// </summary>
        public List<LibraryScopedSkillDocument> ScopedSkillDocuments { get; } = [];

        public Task<IReadOnlyList<LibraryScopedSkillDocument>> ListScopedSkillDocumentsAsync(
            string organizationId,
            LibraryDocumentScopedSkill scopedSkill,
            CancellationToken ct
        ) =>
            Task.FromResult<IReadOnlyList<LibraryScopedSkillDocument>>([
                .. ScopedSkillDocuments.Where(document =>
                    document.OrganizationId == organizationId
                ),
            ]);

        public Task<LibraryScopedSkillDocument?> GetScopedSkillDocumentAsync(
            string organizationId,
            string libraryId,
            string documentId,
            LibraryDocumentScopedSkill scopedSkill,
            CancellationToken ct
        ) =>
            Task.FromResult(
                ScopedSkillDocuments.FirstOrDefault(document =>
                    document.OrganizationId == organizationId
                    && document.LibraryId == libraryId
                    && document.DocumentId == documentId
                )
            );
    }

    /// <summary>
    /// In-memory prompt configuration store keyed on the natural identity the real store upserts by.
    /// </summary>
    private sealed class TestPromptConfigurationStore : ICodeRepositoryPromptConfigurationStore
    {
        public List<CodeRepositoryPromptConfiguration> Rows { get; } = [];

        public Task<IReadOnlyList<CodeRepositoryPromptConfiguration>> ListForRepositoryAsync(
            string organizationId,
            string repositoryId,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult<IReadOnlyList<CodeRepositoryPromptConfiguration>>([
                .. Rows.Where(row =>
                    row.OrganizationId == organizationId && row.RepositoryId == repositoryId
                ),
            ]);

        public Task<CodeRepositoryPromptConfiguration?> FindActiveForPromptAsync(
            string organizationId,
            string repositoryId,
            string libraryId,
            string documentId,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                Rows.FirstOrDefault(row =>
                    row.OrganizationId == organizationId
                    && row.RepositoryId == repositoryId
                    && row.LibraryId == libraryId
                    && row.DocumentId == documentId
                    && row.Active
                )
            );

        public Task<CodeRepositoryPromptConfiguration> UpsertAsync(
            CodeRepositoryPromptConfiguration configuration,
            CancellationToken cancellationToken
        )
        {
            Rows.RemoveAll(row =>
                row.OrganizationId == configuration.OrganizationId
                && row.RepositoryId == configuration.RepositoryId
                && row.LibraryId == configuration.LibraryId
                && row.DocumentId == configuration.DocumentId
            );
            Rows.Add(configuration);

            return Task.FromResult(configuration);
        }
    }
}
