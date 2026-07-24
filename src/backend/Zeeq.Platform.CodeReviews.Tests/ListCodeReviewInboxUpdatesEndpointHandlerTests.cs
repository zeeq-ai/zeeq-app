using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using NSubstitute;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews.Tests;

/// <summary>
/// Handler unit tests for PR inbox update polling.
///
/// Run this test class:
/// dotnet run --project src/backend/Zeeq.Platform.CodeReviews.Tests --output detailed --disable-logo --treenode-filter "/*/*/ListCodeReviewInboxUpdatesEndpointHandlerTests/*"
/// </summary>
public sealed class ListCodeReviewInboxUpdatesEndpointHandlerTests
{
    [Test]
    public async Task ListCodeReviewInboxUpdates_WithMineScopeAndMissingSubject_ReturnsBadRequest()
    {
        var reviews = Substitute.For<ICodeReviewRecordStore>();
        var memberships = Substitute.For<IZeeqMembershipStore>();
        var handler = new ListCodeReviewInboxUpdatesHandler(
            new CodeReviewAuthorization(memberships),
            reviews
        );

        var result = await handler.HandleAsync(
            "org_123",
            teamId: null,
            repositoryId: "repo_123",
            scope: CodeReviewInboxScope.Mine,
            reviewCreatedAtLowerBoundUtc: DateTimeOffset.UtcNow.AddMinutes(-10),
            cursorUpdatedAtUtc: null,
            cursorCreatedAtUtc: null,
            cursorId: null,
            cursorTeamId: null,
            cursorRepositoryId: null,
            cursorScope: null,
            cursorSubjectUserId: null,
            pageSize: null,
            NoSubjectUser(),
            CancellationToken.None
        );

        var badRequest = result.Result as BadRequest<CodeReviewEndpointError>;

        await Assert.That(badRequest).IsNotNull();
        await Assert.That(badRequest!.Value!.Code).IsEqualTo("missing_subject");
        await reviews
            .DidNotReceiveWithAnyArgs()
            .ListInboxUpdatesAsync(default!, CancellationToken.None);
    }

    private static ClaimsPrincipal NoSubjectUser() =>
        new(new ClaimsIdentity(authenticationType: "test"));
}
