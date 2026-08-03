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
/// cookie, the same shape <c>MetricsEndpoints</c> uses — read-only telemetry with no
/// extra membership/role lookup beyond that route-level check.
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
                    ClaimsPrincipal user,
                    [FromServices] ListAgentConversationsHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, cursorStartedAtUtc, cursorId, pageSize, user, ct)
            )
            .WithName("ListAgentConversations")
            .Produces<AgentConversationListResponse>()
            .Produces<AgentConversationEndpointError>(StatusCodes.Status400BadRequest)
            .WithSummary("List my agent conversations.")
            .WithDescription(
                """
                Returns a cursor-paginated page of the caller's own recent agent conversations
                (matched by ingest principal, sign-in email, or an active email alias),
                newest first. There is no "all conversations" option — sharing one conversation
                with a teammate is done by sending them its direct `/sessions/{id}` link, which
                the detail endpoint below serves to any organization member. Pass the cursor
                fields from the previous page (`cursorStartedAtUtc`, `cursorId`) to fetch the
                next page.
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
