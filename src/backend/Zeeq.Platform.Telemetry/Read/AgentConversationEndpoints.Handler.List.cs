using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using OpenIddict.Abstractions;
using Zeeq.Core.Common.AspNetCore.Contracts;
using Zeeq.Core.Models;

namespace Zeeq.Platform.Telemetry.Read;

/// <summary>
/// Handles the Sessions inbox list endpoint.
/// </summary>
public sealed class ListAgentConversationsHandler(IAgentConversationQueryStore conversations)
    : IEndpointHandler
{
    /// <summary>
    /// Lists the caller's recent conversation rows using a partition-aware seek cursor.
    /// </summary>
    /// <remarks>
    /// Always scoped to the authenticated caller — there is no "All" option. Sharing one
    /// conversation with a teammate goes through the unscoped detail endpoint and a direct
    /// link, not through broadening this listing.
    /// </remarks>
    public async Task<
        Results<BadRequest<AgentConversationEndpointError>, Ok<AgentConversationListResponse>>
    > HandleAsync(
        string organizationId,
        DateTimeOffset? cursorStartedAtUtc,
        string? cursorId,
        int? pageSize,
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

        var subjectUserId = user.FindFirstValue(OpenIddictConstants.Claims.Subject);
        if (string.IsNullOrWhiteSpace(subjectUserId))
        {
            return TypedResults.BadRequest(
                new AgentConversationEndpointError(
                    "missing_subject",
                    "Listing conversations requires an authenticated user subject."
                )
            );
        }

        var page = await conversations.ListRecentAsync(
            new AgentConversationStreamQuery(
                OrganizationId: organizationId,
                SubjectUserId: subjectUserId,
                Cursor: AgentConversationEndpointMapping.ToStreamCursor(cursorStartedAtUtc, cursorId),
                PageSize: pageSize ?? 50
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
}
