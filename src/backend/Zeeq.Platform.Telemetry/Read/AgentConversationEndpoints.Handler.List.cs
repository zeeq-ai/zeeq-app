using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using OpenIddict.Abstractions;
using Zeeq.Core.Common.AspNetCore.Contracts;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;

namespace Zeeq.Platform.Telemetry.Read;

/// <summary>
/// Handles the Sessions inbox list endpoint.
/// </summary>
public sealed class ListAgentConversationsHandler(
    IAgentConversationQueryStore conversations,
    IZeeqMembershipStore memberships
) : IEndpointHandler
{
    /// <summary>
    /// Lists one organization member's recent conversation rows using a partition-aware cursor.
    /// </summary>
    /// <remarks>
    /// Defaults to the authenticated caller. An explicit subject is accepted only when its
    /// active membership belongs to <paramref name="organizationId"/>; this keeps the reusable
    /// list contract tenant-scoped without trusting a member id supplied by the browser.
    /// </remarks>
    public async Task<
        Results<BadRequest<AgentConversationEndpointError>, Ok<AgentConversationListResponse>>
    > HandleAsync(
        string organizationId,
        DateTimeOffset? cursorStartedAtUtc,
        string? cursorId,
        int? pageSize,
        string? requestedSubjectUserId,
        decimal? minimumCostUsd,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(organizationId))
        {
            return TypedResults.BadRequest(
                new AgentConversationEndpointError(
                    "missing_organization",
                    "Organization id is required."
                )
            );
        }

        if (minimumCostUsd is < 0 or > 100)
        {
            return TypedResults.BadRequest(
                new AgentConversationEndpointError(
                    "invalid_minimum_cost",
                    "Minimum cost must be between $0 and $100."
                )
            );
        }

        var callerUserId = user.FindFirstValue(OpenIddictConstants.Claims.Subject);
        if (string.IsNullOrWhiteSpace(callerUserId))
        {
            return TypedResults.BadRequest(
                new AgentConversationEndpointError(
                    "missing_subject",
                    "Listing conversations requires an authenticated user subject."
                )
            );
        }

        if (requestedSubjectUserId is not null)
        {
            if (string.IsNullOrWhiteSpace(requestedSubjectUserId))
            {
                return InvalidSubject();
            }

            var membership = await memberships.FindMembershipActivationStateAsync(
                organizationId,
                requestedSubjectUserId,
                cancellationToken
            );

            if (membership?.IsActive != true)
            {
                return InvalidSubject();
            }
        }

        var subjectUserId = requestedSubjectUserId ?? callerUserId;

        var page = await conversations.ListRecentAsync(
            new AgentConversationStreamQuery(
                OrganizationId: organizationId,
                SubjectUserId: subjectUserId,
                Cursor: AgentConversationEndpointMapping.ToStreamCursor(
                    cursorStartedAtUtc,
                    cursorId
                ),
                PageSize: pageSize ?? 50,
                MinimumCostUsd: minimumCostUsd
            ),
            cancellationToken
        );

        return TypedResults.Ok(
            new AgentConversationListResponse(
                page.Items.Select(AgentConversationEndpointMapping.ToDto).ToArray(),
                AgentConversationEndpointMapping.ToDto(page.NextCursor)
            )
        );
    }

    private static BadRequest<AgentConversationEndpointError> InvalidSubject() =>
        TypedResults.BadRequest(
            new AgentConversationEndpointError(
                "invalid_subject",
                "The requested subject must be an active organization member."
            )
        );
}
