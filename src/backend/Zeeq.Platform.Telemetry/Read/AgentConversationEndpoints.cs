using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Zeeq.Core.Common.AspNetCore.Contracts;
using Zeeq.Core.Identity;

namespace Zeeq.Platform.Telemetry.Read;

/// <summary>
/// Conversation inbox and detail endpoints backing the Sessions page.
/// </summary>
/// <remarks>
/// Organization scope comes from the route <c>{orgId}</c> validated against the auth
/// cookie, the same shape <c>MetricsEndpoints</c> uses. The list endpoint additionally
/// validates an explicitly requested subject against active organization membership.
/// </remarks>
public sealed class AgentConversationEndpoints : IEndpoint
{
    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder app, IEndpointRouteBuilder rootApp)
    {
        var group = app.MapGroup("orgs/{orgId}/agent-conversations")
            .WithTags("Sessions")
            .RequireAuthorization(
                new AuthorizeAttribute
                {
                    AuthenticationSchemes = SetupIdentityExtension.CookieScheme,
                }
            )
            .RequireRouteOrganizationMatchesCookie();

        // GET /api/v1/orgs/{orgId}/agent-conversations
        group
            .MapGet(
                "/",
                static (
                    string orgId,
                    [FromQuery] DateTimeOffset? cursorStartedAtUtc,
                    [FromQuery] string? cursorId,
                    [FromQuery] int? pageSize,
                    [FromQuery] string? subjectUserId,
                    [FromQuery] decimal? minimumCostUsd,
                    ClaimsPrincipal user,
                    [FromServices] ListAgentConversationsHandler handler,
                    CancellationToken ct
                ) =>
                    handler.HandleAsync(
                        orgId,
                        cursorStartedAtUtc,
                        cursorId,
                        pageSize,
                        subjectUserId,
                        minimumCostUsd,
                        user,
                        ct
                    )
            )
            .WithName("ListAgentConversations")
            .Produces<AgentConversationListResponse>()
            .Produces<AgentConversationEndpointError>(StatusCodes.Status400BadRequest)
            .WithSummary("List an organization member's agent conversations.")
            .WithDescription(
                """
                Returns a cursor-paginated page of one organization member's recent agent
                conversations (matched by ingest principal, sign-in email, or an active email
                alias), newest first. Omit `subjectUserId` to list the caller's own conversations;
                an explicit subject must be an active member of the route organization. Omit
                `minimumCostUsd` to preserve inbox behavior; pass zero for the known-cost default
                ($0.10), or a value up to 100 for a literal USD floor. Pass the cursor fields from
                the previous page (`cursorStartedAtUtc`, `cursorId`) to fetch the next page.
                """
            );

        // GET /api/v1/orgs/{orgId}/agent-conversations/{conversationId}
        group
            .MapGet(
                "/{conversationId}",
                static (
                    string orgId,
                    string conversationId,
                    [FromServices] GetAgentConversationDetailHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, conversationId, ct)
            )
            .WithName("GetAgentConversationDetail")
            .Produces<AgentConversationDetailResponse>()
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Get one agent conversation's detail.")
            .WithDescription(
                """
                Returns one conversation's summary, prompt timeline, and token/cost usage
                summary. Independently addressable by conversation id and intentionally not
                ownership-scoped, unlike the list endpoint above — any organization member with
                a direct link can open it, so a conversation can be shared by URL.
                """
            );
    }
}
