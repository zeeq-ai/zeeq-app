using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using NSubstitute;
using OpenIddict.Abstractions;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews.Tests;

/// <summary>
/// Handler unit tests for repository review-configuration endpoints.
///
/// Run this test class:
/// dotnet run --project src/backend/Zeeq.Platform.CodeReviews.Tests --output detailed --disable-logo --treenode-filter "/*/*/CodeReviewRepositoryConfigurationEndpointHandlerTests/*"
/// </summary>
public sealed class CodeReviewRepositoryConfigurationEndpointHandlerTests
{
    [Test]
    public async Task GetRepositoryReviewConfiguration_WithMember_ReturnsTypedConfiguration()
    {
        var fixture = Fixture.Create(role: "member");
        fixture.Repositories.Repository.ReviewConfiguration = new()
        {
            FileFilter = new()
            {
                IncludedFiles =
                [
                    new()
                    {
                        MatchType = CodeReviewFileNameMatchType.PathPrefix,
                        Pattern = "src/backend/",
                    },
                ],
            },
        };

        var result = await fixture.GetHandler.HandleAsync(
            "org_123",
            "repo_123",
            TestUser(),
            CancellationToken.None
        );

        var ok = result.Result as Ok<CodeReviewRepositoryConfigurationResponse>;

        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value!.RepositoryId).IsEqualTo("repo_123");
        await Assert
            .That(ok.Value.Configuration.FileFilter.IncludedFiles.Single().Pattern)
            .IsEqualTo("src/backend/");
    }

    [Test]
    public async Task SaveRepositoryReviewConfiguration_WithMember_ReturnsForbid()
    {
        var fixture = Fixture.Create(role: "member");

        var result = await fixture.SaveHandler.HandleAsync(
            "org_123",
            "repo_123",
            SaveRequest(),
            TestUser(),
            CancellationToken.None
        );

        await Assert.That(result.Result is ForbidHttpResult).IsTrue();
        await Assert.That(fixture.Repositories.UpsertCalls).IsEqualTo(0);
    }

    [Test]
    public async Task SaveRepositoryReviewConfiguration_WithAdmin_PreservesRepositoryAndSavesFilter()
    {
        var fixture = Fixture.Create(role: "admin");
        var originalProvider = fixture.Repositories.Repository.Provider;
        var originalOwnerQualifiedName = fixture.Repositories.Repository.OwnerQualifiedName;
        var originalEnabled = fixture.Repositories.Repository.Enabled;

        var result = await fixture.SaveHandler.HandleAsync(
            "org_123",
            "repo_123",
            SaveRequest(includePattern: " src/web/ ", excludePattern: "*.generated.ts"),
            TestUser(),
            CancellationToken.None
        );

        var ok = result.Result as Ok<CodeReviewRepositoryConfigurationResponse>;
        var saved = fixture.Repositories.Repository;

        await Assert.That(ok).IsNotNull();
        await Assert.That(fixture.Repositories.UpsertCalls).IsEqualTo(1);
        await Assert.That(saved.Provider).IsEqualTo(originalProvider);
        await Assert.That(saved.OwnerQualifiedName).IsEqualTo(originalOwnerQualifiedName);
        await Assert.That(saved.Enabled).IsEqualTo(originalEnabled);
        await Assert
            .That(saved.ReviewConfiguration.FileFilter.IncludedFiles.Single().Pattern)
            .IsEqualTo("src/web/");
        await Assert
            .That(saved.ReviewConfiguration.FileFilter.ExcludedFiles.Single().Pattern)
            .IsEqualTo("*.generated.ts");
        await Assert
            .That(ok!.Value!.Configuration.FileFilter.IncludedFiles.Single().MatchType)
            .IsEqualTo(CodeReviewFileNameMatchType.PathPrefix);
    }

    [Test]
    public async Task SaveRepositoryReviewConfiguration_WithBlankPattern_ReturnsBadRequest()
    {
        var fixture = Fixture.Create(role: "owner");

        var result = await fixture.SaveHandler.HandleAsync(
            "org_123",
            "repo_123",
            SaveRequest(includePattern: " "),
            TestUser(),
            CancellationToken.None
        );

        var badRequest = result.Result as BadRequest<CodeReviewEndpointError>;

        await Assert.That(badRequest).IsNotNull();
        await Assert.That(badRequest!.Value!.Code).IsEqualTo("invalid_file_filter");
        await Assert.That(fixture.Repositories.UpsertCalls).IsEqualTo(0);
    }

    [Test]
    public async Task SaveRepositoryReviewConfiguration_WithSharedPromptFragment_RoundTripsValue()
    {
        var fixture = Fixture.Create(role: "admin");

        var result = await fixture.SaveHandler.HandleAsync(
            "org_123",
            "repo_123",
            SaveRequest(sharedPromptFragment: "Always flag missing null checks."),
            TestUser(),
            CancellationToken.None
        );

        var ok = result.Result as Ok<CodeReviewRepositoryConfigurationResponse>;

        await Assert.That(ok).IsNotNull();
        await Assert
            .That(fixture.Repositories.Repository.ReviewConfiguration.SharedPromptFragment)
            .IsEqualTo("Always flag missing null checks.");
        await Assert
            .That(ok!.Value!.Configuration.SharedPromptFragment)
            .IsEqualTo("Always flag missing null checks.");
    }

    [Test]
    public async Task SaveRepositoryReviewConfiguration_WithOverlongSharedPromptFragment_ReturnsBadRequest()
    {
        var fixture = Fixture.Create(role: "admin");

        var result = await fixture.SaveHandler.HandleAsync(
            "org_123",
            "repo_123",
            SaveRequest(sharedPromptFragment: new string('a', 20_001)),
            TestUser(),
            CancellationToken.None
        );

        var badRequest = result.Result as BadRequest<CodeReviewEndpointError>;

        await Assert.That(badRequest).IsNotNull();
        await Assert.That(badRequest!.Value!.Code).IsEqualTo("invalid_shared_prompt_fragment");
        await Assert.That(fixture.Repositories.UpsertCalls).IsEqualTo(0);
    }

    [Test]
    public async Task SaveRepositoryReviewConfiguration_WithMissingFileFilter_ReturnsBadRequest()
    {
        var fixture = Fixture.Create(role: "owner");

        var result = await fixture.SaveHandler.HandleAsync(
            "org_123",
            "repo_123",
            new SaveCodeReviewRepositoryConfigurationRequest(
                new CodeReviewRepositoryConfigurationDto(FileFilter: null!)
            ),
            TestUser(),
            CancellationToken.None
        );

        var badRequest = result.Result as BadRequest<CodeReviewEndpointError>;

        await Assert.That(badRequest).IsNotNull();
        await Assert.That(badRequest!.Value!.Code).IsEqualTo("invalid_file_filter");
        await Assert.That(fixture.Repositories.UpsertCalls).IsEqualTo(0);
    }

    private static SaveCodeReviewRepositoryConfigurationRequest SaveRequest(
        string includePattern = "src/backend/",
        string excludePattern = "bin/",
        string? sharedPromptFragment = null
    ) =>
        new(
            new CodeReviewRepositoryConfigurationDto(
                new CodeReviewFileFilterDto(
                    [
                        new CodeReviewFileMatchCriteriaDto(
                            CodeReviewFileNameMatchType.PathPrefix,
                            includePattern
                        ),
                    ],
                    [
                        new CodeReviewFileMatchCriteriaDto(
                            CodeReviewFileNameMatchType.Glob,
                            excludePattern
                        ),
                    ]
                )
            )
            {
                SharedPromptFragment = sharedPromptFragment,
            }
        );

    private static ClaimsPrincipal TestUser() =>
        new(
            new ClaimsIdentity(
                [new Claim(OpenIddictConstants.Claims.Subject, "usr_123")],
                authenticationType: "test"
            )
        );

    private sealed class Fixture
    {
        private Fixture() { }

        public TestCodeRepositoryStore Repositories { get; } = new();

        public GetRepositoryReviewConfigurationHandler GetHandler { get; private set; } = null!;

        public SaveRepositoryReviewConfigurationHandler SaveHandler { get; private set; } = null!;

        public static Fixture Create(string role)
        {
            var fixture = new Fixture();
            var memberships = Substitute.For<IZeeqMembershipStore>();
            memberships
                .ListActiveMembershipsForUserAsync("usr_123", Arg.Any<CancellationToken>())
                .Returns(
                    Task.FromResult<IReadOnlyList<OrganizationMembership>>([
                        new()
                        {
                            Id = "mem_123",
                            OrganizationId = "org_123",
                            UserId = "usr_123",
                            Role = role,
                            Status = MembershipStatus.Active,
                            CreatedByUserId = "usr_123",
                        },
                    ])
                );
            var authorization = new CodeReviewAuthorization(memberships);

            fixture.GetHandler = new(authorization, fixture.Repositories);
            fixture.SaveHandler = new(authorization, fixture.Repositories);

            return fixture;
        }
    }

    private sealed class TestCodeRepositoryStore : ICodeRepositoryStore
    {
        public int UpsertCalls { get; private set; }

        public CodeRepository Repository { get; set; } =
            new()
            {
                Id = "repo_123",
                OrganizationId = "org_123",
                TeamId = "team_123",
                Provider = "github",
                OwnerQualifiedName = "zeeq-ai/zeeq",
                DisplayName = "zeeq-ai/zeeq",
                Enabled = true,
                ReviewConfiguration = CodeRepositoryReviewConfiguration.Empty,
                CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
                UpdatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
            };

        public Task<CodeRepository?> FindActiveAsync(
            string provider,
            string ownerQualifiedName,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Provider lookup is not used by these tests.");

        public Task<IReadOnlyList<CodeRepository>> ListActiveForOrganizationAsync(
            string organizationId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Repository listing is not used by these tests.");

        public Task<IReadOnlyList<CodeRepository>> ListConfiguredForOrganizationAsync(
            string organizationId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Repository listing is not used by these tests.");

        public Task<CodeRepository?> FindActiveForOrganizationAsync(
            string organizationId,
            string repositoryId,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                Repository.OrganizationId == organizationId && Repository.Id == repositoryId
                    ? Repository
                    : null
            );

        public Task<CodeRepository> UpsertAsync(
            CodeRepository repository,
            CancellationToken cancellationToken
        )
        {
            UpsertCalls++;
            Repository = repository;

            return Task.FromResult(repository);
        }

        public Task<bool> DisableAsync(
            string organizationId,
            string repositoryId,
            DateTimeOffset disabledAtUtc,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Repository disabling is not used by these tests.");
    }
}
