using System.Security.Claims;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using NSubstitute;
using OpenIddict.Abstractions;

namespace Zeeq.Platform.CodeReviews.Tests;

/// <summary>
/// Handler unit tests for organization-level code-review settings endpoints.
///
/// Run this test class:
/// dotnet run --project src/backend/Zeeq.Platform.CodeReviews.Tests --output detailed --disable-logo --treenode-filter "/*/*/CodeReviewOrganizationSettingsEndpointHandlerTests/*"
/// </summary>
public sealed class CodeReviewOrganizationSettingsEndpointHandlerTests
{
    [Test]
    public async Task GetCodeReviewOrganizationSettings_WithAdmin_ReturnsSettings()
    {
        var fixture = Fixture.Create(role: "admin");
        fixture.SettingsStore.Settings = fixture.SettingsStore.Settings with
        {
            MaxConcurrentReviews = 4,
        };

        var result = await fixture.GetHandler.HandleAsync(
            "org_123",
            TestUser(),
            CancellationToken.None
        );

        var ok = result.Result as Ok<CodeReviewOrganizationSettingsResponse>;

        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value!.OrganizationId).IsEqualTo("org_123");
        await Assert.That(ok.Value.MaxConcurrentReviews).IsEqualTo(4);
        await Assert.That(ok.Value.ExecutionLeaseDurationMinutes).IsEqualTo(2);
    }

    [Test]
    public async Task GetCodeReviewOrganizationSettings_WithMember_ReturnsForbid()
    {
        var fixture = Fixture.Create(role: "member");

        var result = await fixture.GetHandler.HandleAsync(
            "org_123",
            TestUser(),
            CancellationToken.None
        );

        await Assert.That(result.Result is ForbidHttpResult).IsTrue();
    }

    [Test]
    public async Task SaveCodeReviewOrganizationSettings_WithOwner_UpdatesConcurrencyOnly()
    {
        var fixture = Fixture.Create(role: "owner");
        var originalLeaseDuration = fixture.SettingsStore.Settings.ExecutionLeaseDuration;

        var result = await fixture.SaveHandler.HandleAsync(
            "org_123",
            new SaveCodeReviewOrganizationSettingsRequest(MaxConcurrentReviews: 4),
            TestUser(),
            CancellationToken.None
        );

        var ok = result.Result as Ok<CodeReviewOrganizationSettingsResponse>;

        await Assert.That(ok).IsNotNull();
        await Assert.That(fixture.SettingsStore.SaveCalls).IsEqualTo(1);
        await Assert.That(fixture.SettingsStore.Settings.MaxConcurrentReviews).IsEqualTo(4);
        await Assert
            .That(fixture.SettingsStore.Settings.ExecutionLeaseDuration)
            .IsEqualTo(originalLeaseDuration);
        await Assert.That(ok!.Value!.MaxConcurrentReviews).IsEqualTo(4);
    }

    [Test]
    public async Task SaveCodeReviewOrganizationSettings_WithInvalidConcurrency_ReturnsBadRequest()
    {
        var fixture = Fixture.Create(role: "admin");

        var result = await fixture.SaveHandler.HandleAsync(
            "org_123",
            new SaveCodeReviewOrganizationSettingsRequest(MaxConcurrentReviews: 0),
            TestUser(),
            CancellationToken.None
        );

        var badRequest = result.Result as BadRequest<CodeReviewEndpointError>;

        await Assert.That(badRequest).IsNotNull();
        await Assert.That(badRequest!.Value!.Code).IsEqualTo("invalid_code_review_settings");
        await Assert.That(fixture.SettingsStore.SaveCalls).IsEqualTo(0);
    }

    [Test]
    public async Task SaveCodeReviewOrganizationSettings_WithMember_ReturnsForbid()
    {
        var fixture = Fixture.Create(role: "member");

        var result = await fixture.SaveHandler.HandleAsync(
            "org_123",
            new SaveCodeReviewOrganizationSettingsRequest(MaxConcurrentReviews: 2),
            TestUser(),
            CancellationToken.None
        );

        await Assert.That(result.Result is ForbidHttpResult).IsTrue();
        await Assert.That(fixture.SettingsStore.SaveCalls).IsEqualTo(0);
    }

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

        public TestCodeReviewOrganizationSettingsStore SettingsStore { get; } = new();

        public GetCodeReviewOrganizationSettingsHandler GetHandler { get; private set; } = null!;

        public SaveCodeReviewOrganizationSettingsHandler SaveHandler { get; private set; } = null!;

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

            fixture.GetHandler = new(authorization, fixture.SettingsStore);
            fixture.SaveHandler = new(authorization, fixture.SettingsStore);

            return fixture;
        }
    }

    private sealed class TestCodeReviewOrganizationSettingsStore
        : ICodeReviewOrganizationSettingsStore
    {
        public int SaveCalls { get; private set; }

        public CodeReviewOrganizationSettings Settings { get; set; } =
            new() { MaxConcurrentReviews = 2, ExecutionLeaseDuration = TimeSpan.FromMinutes(2) };

        public Task<CodeReviewOrganizationSettings> GetAsync(
            string organizationId,
            CancellationToken cancellationToken
        ) => Task.FromResult(Settings);

        public Task<CodeReviewOrganizationSettings> SaveAsync(
            string organizationId,
            CodeReviewOrganizationSettings settings,
            CancellationToken cancellationToken
        )
        {
            SaveCalls++;
            Settings = settings;

            return Task.FromResult(settings);
        }
    }
}
