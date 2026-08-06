using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using NSubstitute;
using OpenIddict.Abstractions;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;
using Zeeq.Platform.Telemetry.Read;

namespace Zeeq.Platform.Telemetry.Tests;

/// <summary>
/// Verifies subject resolution and membership authorization for the conversation list endpoint.
/// </summary>
/// <remarks>
/// Run this test class:
/// <c>dotnet run --project src/backend/Zeeq.Platform.Telemetry.Tests --output detailed
/// --disable-logo --treenode-filter "/*/*/ListAgentConversationsHandlerTests/*"</c>.
/// </remarks>
public sealed class ListAgentConversationsHandlerTests
{
    [Test]
    public async Task ListAgentConversations_WithoutRequestedSubject_DefaultsToCaller()
    {
        var fixture = Fixture.Create();

        var result = await fixture.Handler.HandleAsync(
            "org_123",
            cursorStartedAtUtc: null,
            cursorId: null,
            pageSize: null,
            requestedSubjectUserId: null,
            minimumCostUsd: null,
            TestUser("usr_caller"),
            CancellationToken.None
        );

        await Assert.That(result.Result).IsTypeOf<Ok<AgentConversationListResponse>>();
        await Assert.That(fixture.Conversations.LastQuery).IsNotNull();
        await Assert.That(fixture.Conversations.LastQuery!.SubjectUserId).IsEqualTo("usr_caller");
        await Assert.That(fixture.Conversations.LastQuery.MinimumCostUsd).IsNull();
        await fixture
            .Memberships.DidNotReceive()
            .FindMembershipActivationStateAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Test]
    public async Task ListAgentConversations_WithActiveMemberSubject_UsesRequestedSubject()
    {
        var fixture = Fixture.Create();
        fixture
            .Memberships.FindMembershipActivationStateAsync(
                "org_123",
                "usr_member",
                Arg.Any<CancellationToken>()
            )
            .Returns(
                new MembershipActivationState(
                    "org_123",
                    "usr_member",
                    MembershipStatus.Active,
                    DisabledAtIsSet: false
                )
            );

        var result = await fixture.Handler.HandleAsync(
            "org_123",
            cursorStartedAtUtc: null,
            cursorId: null,
            pageSize: 100,
            requestedSubjectUserId: "usr_member",
            minimumCostUsd: 5m,
            TestUser("usr_caller"),
            CancellationToken.None
        );

        await Assert.That(result.Result).IsTypeOf<Ok<AgentConversationListResponse>>();
        await Assert.That(fixture.Conversations.LastQuery).IsNotNull();
        await Assert.That(fixture.Conversations.LastQuery!.SubjectUserId).IsEqualTo("usr_member");
        await Assert.That(fixture.Conversations.LastQuery.PageSize).IsEqualTo(100);
        await Assert.That(fixture.Conversations.LastQuery.MinimumCostUsd).IsEqualTo(5m);
    }

    [Test]
    public async Task ListAgentConversations_WithInactiveMemberSubject_ReturnsBadRequest()
    {
        var fixture = Fixture.Create();
        fixture
            .Memberships.FindMembershipActivationStateAsync(
                "org_123",
                "usr_inactive",
                Arg.Any<CancellationToken>()
            )
            .Returns(
                new MembershipActivationState(
                    "org_123",
                    "usr_inactive",
                    MembershipStatus.Active,
                    DisabledAtIsSet: true
                )
            );

        var result = await fixture.Handler.HandleAsync(
            "org_123",
            cursorStartedAtUtc: null,
            cursorId: null,
            pageSize: null,
            requestedSubjectUserId: "usr_inactive",
            minimumCostUsd: 0m,
            TestUser("usr_caller"),
            CancellationToken.None
        );

        var badRequest = result.Result as BadRequest<AgentConversationEndpointError>;

        await Assert.That(badRequest).IsNotNull();
        await Assert.That(badRequest!.Value!.Code).IsEqualTo("invalid_subject");
        await Assert.That(fixture.Conversations.LastQuery).IsNull();
    }

    [Test]
    public async Task ListAgentConversations_WithMissingCallerSubject_ReturnsBadRequest()
    {
        var fixture = Fixture.Create();

        var result = await fixture.Handler.HandleAsync(
            "org_123",
            cursorStartedAtUtc: null,
            cursorId: null,
            pageSize: null,
            requestedSubjectUserId: "usr_member",
            minimumCostUsd: null,
            new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "test")),
            CancellationToken.None
        );

        var badRequest = result.Result as BadRequest<AgentConversationEndpointError>;

        await Assert.That(badRequest).IsNotNull();
        await Assert.That(badRequest!.Value!.Code).IsEqualTo("missing_subject");
        await Assert.That(fixture.Conversations.LastQuery).IsNull();
    }

    [Test]
    public async Task ListAgentConversations_WithOutOfRangeMinimumCost_ReturnsBadRequest()
    {
        var fixture = Fixture.Create();

        var result = await fixture.Handler.HandleAsync(
            "org_123",
            cursorStartedAtUtc: null,
            cursorId: null,
            pageSize: null,
            requestedSubjectUserId: null,
            minimumCostUsd: 105m,
            TestUser("usr_caller"),
            CancellationToken.None
        );

        var badRequest = result.Result as BadRequest<AgentConversationEndpointError>;

        await Assert.That(badRequest).IsNotNull();
        await Assert.That(badRequest!.Value!.Code).IsEqualTo("invalid_minimum_cost");
        await Assert.That(fixture.Conversations.LastQuery).IsNull();
    }

    [Test]
    public async Task ListAgentConversations_TruncatesTitleTo64Characters()
    {
        var fixture = Fixture.Create();
        fixture.Conversations.Page = new AgentConversationStreamPage(
            [
                new AgentConversationSummary(
                    "conversation_123",
                    "codex",
                    null,
                    null,
                    null,
                    "caller@example.com",
                    "usr_caller",
                    DateTimeOffset.UtcNow,
                    null,
                    new string('x', 120),
                    AgentConversationRollupStatus.Ready,
                    100,
                    10,
                    1m
                ),
            ],
            null
        );

        var result = await fixture.Handler.HandleAsync(
            "org_123",
            cursorStartedAtUtc: null,
            cursorId: null,
            pageSize: null,
            requestedSubjectUserId: null,
            minimumCostUsd: null,
            TestUser("usr_caller"),
            CancellationToken.None
        );

        var ok = result.Result as Ok<AgentConversationListResponse>;

        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value!.Items.Single().Title).IsEqualTo($"{new string('x', 63)}…");
    }

    private static ClaimsPrincipal TestUser(string userId) =>
        new(
            new ClaimsIdentity(
                [new Claim(OpenIddictConstants.Claims.Subject, userId)],
                authenticationType: "test"
            )
        );

    private sealed class Fixture
    {
        private Fixture() { }

        public TestAgentConversationQueryStore Conversations { get; } = new();

        public IZeeqMembershipStore Memberships { get; } = Substitute.For<IZeeqMembershipStore>();

        public ListAgentConversationsHandler Handler { get; private set; } = null!;

        public static Fixture Create()
        {
            var fixture = new Fixture();
            fixture.Handler = new(fixture.Conversations, fixture.Memberships);

            return fixture;
        }
    }

    private sealed class TestAgentConversationQueryStore : IAgentConversationQueryStore
    {
        public AgentConversationStreamQuery? LastQuery { get; private set; }

        public AgentConversationStreamPage Page { get; set; } = new([], null);

        public Task<AgentConversationStreamPage> ListRecentAsync(
            AgentConversationStreamQuery query,
            CancellationToken cancellationToken
        )
        {
            LastQuery = query;

            return Task.FromResult(Page);
        }

        public Task<AgentConversationDetail?> GetDetailAsync(
            string organizationId,
            string conversationId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Detail lookup is not used by these tests.");
    }
}
