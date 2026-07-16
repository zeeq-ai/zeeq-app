using System.ComponentModel.DataAnnotations;
using Zeeq.Core.Models;
using Microsoft.AspNetCore.Authorization;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Pull request and review history endpoints for the code-review inbox.
/// </summary>
public sealed class CodeReviewEndpoints : IEndpoint
{
    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder app, IEndpointRouteBuilder rootApp)
    {
        var group = app.MapGroup("orgs/{orgId}/code-reviews")
            .WithTags("Code Reviews")
            .RequireAuthorization(
                new AuthorizeAttribute
                {
                    AuthenticationSchemes = SetupIdentityExtension.CookieScheme,
                }
            )
            .RequireRouteOrganizationMatchesCookie();

        // GET /api/v1/orgs/{orgId}/code-reviews/pull-requests
        group
            .MapGet(
                "/pull-requests",
                static (
                    string orgId,
                    [FromQuery] string? teamId,
                    [FromQuery] string? repositoryId,
                    [FromQuery] PullRequestClaimStatus? claimStatus,
                    [FromQuery] DateTimeOffset? cursorCreatedAtUtc,
                    [FromQuery] string? cursorId,
                    [FromQuery] int? pageSize,
                    ClaimsPrincipal user,
                    [FromServices] ListCodeReviewPullRequestsHandler handler,
                    CancellationToken ct
                ) =>
                    handler.HandleAsync(
                        orgId,
                        teamId,
                        repositoryId,
                        claimStatus,
                        cursorCreatedAtUtc,
                        cursorId,
                        pageSize,
                        user,
                        ct
                    )
            )
            .WithName("ListCodeReviewPullRequests")
            .Produces<CodeReviewPullRequestListResponse>()
            .Produces<CodeReviewEndpointError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("List pull requests.")
            .WithDescription(
                """
                Returns a cursor-paginated page of recent pull requests in the organization's
                code-review inbox. Results can be narrowed by `teamId`, `repositoryId`, and
                `claimStatus`. Pass the cursor fields from the previous page
                (`cursorCreatedAtUtc`, `cursorId`) to fetch the next page.

                Scoped to the organization identified by the route `orgId`.
                """
            );

        // GET /api/v1/orgs/{orgId}/code-reviews/pull-requests/by-number
        // NOTE: Must be registered before the parameterized /{pullRequestRecordId} route.
        // ASP.NET Core routing scores literal segments above parameter segments regardless
        // of registration order, but the explicit ordering documents intent.
        group
            .MapGet(
                "/pull-requests/by-number",
                static (
                    [MaxLength(36)] string orgId,
                    [FromQuery] string? repositoryId,
                    [FromQuery, Range(1, 999_999)] int? pullRequestNumber,
                    ClaimsPrincipal user,
                    [FromServices] GetPullRequestByNumberHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, repositoryId, pullRequestNumber, user, ct)
            )
            .WithName("GetPullRequestByNumber")
            .Produces<CodeReviewPullRequestDetailResponse>()
            .Produces<CodeReviewEndpointError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Find a pull request by provider number.")
            .WithDescription(
                """
                Resolves a single pull request by its repo-scoped provider number
                (e.g. GitHub PR #42) within the route `orgId`. Both `repositoryId` and
                `pullRequestNumber` are required because PR numbers are only unique within
                a repository. Returns the resolved PR detail row so the frontend can
                inject it into the inbox without a separate lookup round-trip.
                """
            );

        // GET /api/v1/orgs/{orgId}/code-reviews/pull-requests/{pullRequestRecordId}
        group
            .MapGet(
                "/pull-requests/{pullRequestRecordId}",
                static async (
                    string pullRequestRecordId,
                    string orgId,
                    [FromQuery] string? c,
                    ClaimsPrincipal user,
                    [FromServices] GetCodeReviewPullRequestHandler handler,
                    CancellationToken ct
                ) =>
                {
                    if (!TryDecodeSingleReviewToken(c, out var createdAtUtc, out _, out var error))
                    {
                        return TypedResults.BadRequest(error!);
                    }

                    return await handler.HandleAsync(
                        orgId,
                        pullRequestRecordId,
                        createdAtUtc,
                        user,
                        ct
                    );
                }
            )
            .WithName("GetCodeReviewPullRequest")
            .Produces<CodeReviewPullRequestDetailResponse>()
            .Produces<CodeReviewEndpointError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Get a pull request.")
            .WithDescription(
                """
                Returns the detail view for a single pull request identified by
                `pullRequestRecordId` within the route `orgId`. The `c` query value is a compact,
                URL-safe token encoding the record's partition timestamp; it is carried on every
                PR DTO as `singleViewToken`. A malformed token yields a 400 `invalid_token`;
                an omitted token yields a 400 `missing_token`.
                """
            );

        // GET /api/v1/orgs/{orgId}/code-reviews/pull-requests/{pullRequestRecordId}/reviews
        group
            .MapGet(
                "/pull-requests/{pullRequestRecordId}/reviews",
                static (
                    string pullRequestRecordId,
                    string orgId,
                    [FromQuery] DateTimeOffset? createdAtUtc,
                    [FromQuery] DateTimeOffset? cursorCreatedAtUtc,
                    [FromQuery] string? cursorId,
                    [FromQuery] int? pageSize,
                    ClaimsPrincipal user,
                    [FromServices] ListPullRequestCodeReviewsHandler handler,
                    CancellationToken ct
                ) =>
                    handler.HandleAsync(
                        orgId,
                        pullRequestRecordId,
                        createdAtUtc,
                        cursorCreatedAtUtc,
                        cursorId,
                        pageSize,
                        user,
                        ct
                    )
            )
            .WithName("ListPullRequestCodeReviews")
            .Produces<CodeReviewPullRequestReviewListResponse>()
            .Produces<CodeReviewEndpointError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("List a pull request's reviews.")
            .WithDescription(
                """
                Returns the cursor-paginated review history for one pull request — each
                automated or manually requested review run recorded against
                `pullRequestRecordId`, newest first.

                Scoped to the route `orgId`; pass `createdAtUtc` to pin the parent PR's partition and
                the cursor fields to page.
                """
            );

        // GET /api/v1/orgs/{orgId}/code-reviews/reviews/{codeReviewRecordId}/findings
        group
            .MapGet(
                "/reviews/{codeReviewRecordId}/findings",
                static (
                    string codeReviewRecordId,
                    string orgId,
                    [FromQuery] DateTimeOffset? createdAtUtc,
                    ClaimsPrincipal user,
                    [FromServices] GetCodeReviewFindingsHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, codeReviewRecordId, createdAtUtc, user, ct)
            )
            .WithName("GetCodeReviewFindings")
            .Produces<CodeReviewFindingsResponse>()
            .Produces<CodeReviewEndpointError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Get review findings.")
            .WithDescription(
                """
                Returns the parsed findings produced by a single review run, identified by
                `codeReviewRecordId` within the route `orgId`. This is the detailed per-review payload
                (individual findings) behind a row from the review history list.

                Pass `createdAtUtc` to locate the record's partition directly.
                """
            );

        // GET /api/v1/orgs/{orgId}/code-reviews/reviews/{codeReviewRecordId}
        group
            .MapGet(
                "/reviews/{codeReviewRecordId}",
                static async Task<
                    Results<
                        NotFound,
                        BadRequest<CodeReviewEndpointError>,
                        Ok<CodeReviewSingleViewResponse>
                    >
                > (
                    string codeReviewRecordId,
                    string orgId,
                    [FromQuery] string? c,
                    ClaimsPrincipal user,
                    [FromServices] GetCodeReviewHandler handler,
                    CancellationToken ct
                ) =>
                {
                    // Route binding supplies null when `c` is omitted; keep the public
                    // missing-token and malformed-token distinction at the endpoint boundary.
                    if (
                        !TryDecodeSingleReviewToken(
                            c,
                            out var createdAtUtc,
                            out var mode,
                            out var error
                        )
                    )
                    {
                        return TypedResults.BadRequest(error!);
                    }

                    return await handler.HandleAsync(
                        orgId,
                        codeReviewRecordId,
                        createdAtUtc,
                        mode,
                        user,
                        ct
                    );
                }
            )
            .WithName("GetCodeReview")
            .Produces<CodeReviewSingleViewResponse>()
            .Produces<CodeReviewEndpointError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Get a single code review.")
            .WithDescription(
                """
                Returns one review row plus the related set to display in the single-review view.
                The `c` query value is a compact, URL-safe token (built by the backend) that packs
                the record's partition timestamp with the view mode: agent mode returns the agent
                session's reviews, pr mode returns the parent PR's review history. Scoped to the
                route `orgId`. A malformed token yields a 400 `invalid_token`; an omitted
                token yields a 400 `missing_token`.
                """
            );

        // POST /api/v1/orgs/{orgId}/code-reviews/pull-requests/{pullRequestRecordId}/reviews/request
        group
            .MapPost(
                "/pull-requests/{pullRequestRecordId}/reviews/request",
                static (
                    string pullRequestRecordId,
                    string orgId,
                    [FromQuery] DateTimeOffset? createdAtUtc,
                    ClaimsPrincipal user,
                    [FromServices] RequestCodeReviewHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, pullRequestRecordId, createdAtUtc, user, ct)
            )
            .RequireActiveOrganization()
            .WithName("RequestCodeReview")
            .Produces<CodeReviewManualRequestResponse>()
            .Produces<CodeReviewEndpointError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Request a review.")
            .WithDescription(
                """
                Manually queues a new review for an existing pull request through the same
                durable workflow that webhooks use. Because the user asked explicitly, it
                bypasses the draft and webhook-action gates, but still enforces repository
                visibility, closed-PR, budget, and active-review guards.

                Scoped to the route `orgId`; pass `createdAtUtc` to target the PR's partition.
                """
            );

        // GET /api/v1/orgs/{orgId}/code-reviews/organization-settings
        group
            .MapGet(
                "/organization-settings",
                static (
                    string orgId,
                    ClaimsPrincipal user,
                    [FromServices] GetCodeReviewOrganizationSettingsHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, user, ct)
            )
            .WithName("GetCodeReviewOrganizationSettings")
            .Produces<CodeReviewOrganizationSettingsResponse>()
            .Produces<CodeReviewEndpointError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Get org review settings.")
            .WithDescription(
                """
                Returns the organization-wide code-review execution settings for the route `orgId` —
                the defaults that govern how reviews run across every repository in the org.
                """
            );

        // PUT /api/v1/orgs/{orgId}/code-reviews/organization-settings
        group
            .MapPut(
                "/organization-settings",
                static (
                    string orgId,
                    [FromBody] SaveCodeReviewOrganizationSettingsRequest request,
                    ClaimsPrincipal user,
                    [FromServices] SaveCodeReviewOrganizationSettingsHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, request, user, ct)
            )
            .RequireActiveOrganization()
            .WithName("SaveCodeReviewOrganizationSettings")
            .Produces<CodeReviewOrganizationSettingsResponse>()
            .Produces<CodeReviewEndpointError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Save org review settings.")
            .WithDescription(
                """
                Replaces the organization-wide code-review execution settings for the route `orgId`.
                These defaults apply to every repository unless overridden by a
                repository-level configuration.

                Restricted to organization owners and admins (`403` otherwise).
                """
            );

        // GET /api/v1/orgs/{orgId}/code-reviews/repositories/{repositoryId}/agents
        group
            .MapGet(
                "/repositories/{repositoryId}/agents",
                static (
                    string repositoryId,
                    string orgId,
                    ClaimsPrincipal user,
                    [FromServices] ListRepositoryCodeReviewerAgentsHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, repositoryId, user, ct)
            )
            .WithName("ListRepositoryCodeReviewerAgents")
            .Produces<CodeReviewerAgentListResponse>()
            .Produces<CodeReviewEndpointError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("List reviewer agents.")
            .WithDescription(
                """
                Returns the reviewer agents configured for the repository (`repositoryId`)
                within the route `orgId`. Reviewer agents are the persisted personas/configurations that
                perform automated reviews for that repository.
                """
            );

        // GET /api/v1/orgs/{orgId}/code-reviews/agent-templates
        group
            .MapGet(
                "/agent-templates",
                static (
                    string orgId,
                    ClaimsPrincipal user,
                    [FromServices] ListCodeReviewerAgentTemplatesHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, user, ct)
            )
            .WithName("ListCodeReviewerAgentTemplates")
            .Produces<CodeReviewerAgentTemplateListResponse>()
            .Produces<CodeReviewEndpointError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("List reviewer agent templates.")
            .WithDescription(
                """
                Returns the built-in, clonable reviewer-agent templates within the route `orgId`.
                Templates are code-defined starting points (personas) used to seed a new agent's
                configuration; they are not persisted and are never tied to a specific repository.

                Restricted to organization owners and admins (`403` otherwise).
                """
            );

        // POST /api/v1/orgs/{orgId}/code-reviews/repositories/{repositoryId}/agents
        group
            .MapPost(
                "/repositories/{repositoryId}/agents",
                static (
                    string repositoryId,
                    string orgId,
                    [FromBody] CreateCodeReviewerAgentRequest request,
                    ClaimsPrincipal user,
                    [FromServices] CreateRepositoryCodeReviewerAgentHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, repositoryId, request, user, ct)
            )
            .RequireActiveOrganization()
            .WithName("CreateRepositoryCodeReviewerAgent")
            .Produces<CodeReviewerAgentResponse>()
            .Produces<CodeReviewEndpointError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Create a reviewer agent.")
            .WithDescription(
                """
                Adds a new reviewer agent to the repository (`repositoryId`) within the route `orgId`.
                The agent becomes eligible to run on subsequent reviews for that repository.

                Restricted to organization owners and admins (`403` otherwise).
                """
            );

        // PUT /api/v1/orgs/{orgId}/code-reviews/repositories/{repositoryId}/agents/{agentId}
        group
            .MapPut(
                "/repositories/{repositoryId}/agents/{agentId}",
                static (
                    string repositoryId,
                    string agentId,
                    string orgId,
                    [FromBody] UpdateCodeReviewerAgentRequest request,
                    ClaimsPrincipal user,
                    [FromServices] UpdateRepositoryCodeReviewerAgentHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, repositoryId, agentId, request, user, ct)
            )
            .RequireActiveOrganization()
            .WithName("UpdateRepositoryCodeReviewerAgent")
            .Produces<CodeReviewerAgentResponse>()
            .Produces<CodeReviewEndpointError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Update a reviewer agent.")
            .WithDescription(
                """
                Updates the reviewer agent identified by `agentId` on repository
                `repositoryId` within the route `orgId`. Changes apply to reviews that run after the
                update.

                Restricted to organization owners and admins (`403` otherwise).
                """
            );

        // DELETE /api/v1/orgs/{orgId}/code-reviews/repositories/{repositoryId}/agents/{agentId}
        group
            .MapDelete(
                "/repositories/{repositoryId}/agents/{agentId}",
                static (
                    string repositoryId,
                    string agentId,
                    string orgId,
                    ClaimsPrincipal user,
                    [FromServices] DeleteRepositoryCodeReviewerAgentHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, repositoryId, agentId, user, ct)
            )
            .RequireActiveOrganization()
            .WithName("DeleteRepositoryCodeReviewerAgent")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<CodeReviewEndpointError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Delete a reviewer agent.")
            .WithDescription(
                """
                Disables the reviewer agent identified by `agentId` on repository
                `repositoryId` within the route `orgId` so it no longer participates in future reviews.
                Past reviews it produced are retained.

                Restricted to organization owners and admins (`403` otherwise).
                """
            );

        // GET /api/v1/orgs/{orgId}/code-reviews/review-updates
        group
            .MapGet(
                "/review-updates",
                static (
                    string orgId,
                    [FromQuery] string? teamId,
                    [FromQuery] string? repositoryId,
                    [FromQuery] CodeReviewInboxScope? scope,
                    [FromQuery] DateTimeOffset? reviewCreatedAtLowerBoundUtc,
                    [FromQuery] DateTimeOffset? cursorUpdatedAtUtc,
                    [FromQuery] DateTimeOffset? cursorCreatedAtUtc,
                    [FromQuery] string? cursorId,
                    [FromQuery] string? cursorTeamId,
                    [FromQuery] string? cursorRepositoryId,
                    [FromQuery] CodeReviewInboxScope? cursorScope,
                    [FromQuery] string? cursorSubjectUserId,
                    [FromQuery] int? pageSize,
                    ClaimsPrincipal user,
                    [FromServices] ListCodeReviewInboxUpdatesHandler handler,
                    CancellationToken ct
                ) =>
                    handler.HandleAsync(
                        orgId,
                        teamId,
                        repositoryId,
                        scope,
                        reviewCreatedAtLowerBoundUtc,
                        cursorUpdatedAtUtc,
                        cursorCreatedAtUtc,
                        cursorId,
                        cursorTeamId,
                        cursorRepositoryId,
                        cursorScope,
                        cursorSubjectUserId,
                        pageSize,
                        user,
                        ct
                    )
            )
            .WithName("ListCodeReviewInboxUpdates")
            .Produces<CodeReviewInboxUpdateListResponse>()
            .Produces<CodeReviewEndpointError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Poll inbox review updates.")
            .WithDescription(
                """
                Returns a lightweight, cursor-paginated feed of recent review updates for the
                inbox, designed for frequent polling. Filter by `teamId`, `repositoryId`, and
                `scope`, and use `reviewCreatedAtLowerBoundUtc` to bound how far back to look.

                Each payload is intentionally minimal (status/timestamps) so clients can detect
                changes cheaply and fetch full detail only when needed. Scoped to the route `orgId`.
                """
            );

        // GET /api/v1/orgs/{orgId}/code-reviews/repositories/{repositoryId}/review-configuration
        group
            .MapGet(
                "/repositories/{repositoryId}/review-configuration",
                static (
                    string repositoryId,
                    string orgId,
                    ClaimsPrincipal user,
                    [FromServices] GetRepositoryReviewConfigurationHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, repositoryId, user, ct)
            )
            .WithName("GetCodeReviewRepositoryConfiguration")
            .Produces<CodeReviewRepositoryConfigurationResponse>()
            .Produces<CodeReviewEndpointError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Get repository review config.")
            .WithDescription(
                """
                Returns the repository-level code-review configuration for `repositoryId`
                within the route `orgId`. These per-repository settings layer on top of the
                organization defaults.
                """
            );

        // PUT /api/v1/orgs/{orgId}/code-reviews/repositories/{repositoryId}/review-configuration
        group
            .MapPut(
                "/repositories/{repositoryId}/review-configuration",
                static (
                    string repositoryId,
                    string orgId,
                    [FromBody] SaveCodeReviewRepositoryConfigurationRequest request,
                    ClaimsPrincipal user,
                    [FromServices] SaveRepositoryReviewConfigurationHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, repositoryId, request, user, ct)
            )
            .RequireActiveOrganization()
            .WithName("SaveCodeReviewRepositoryConfiguration")
            .Produces<CodeReviewRepositoryConfigurationResponse>()
            .Produces<CodeReviewEndpointError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Save repository review config.")
            .WithDescription(
                """
                Replaces the repository-level code-review configuration for `repositoryId`
                within the route `orgId`, overriding the organization defaults for this repository only.

                Restricted to organization owners and admins (`403` otherwise).
                """
            );

        // POST /api/v1/orgs/{orgId}/code-reviews/repositories/{repositoryId}/pull-requests/{pullRequestNumber}/check-run/bypass
        group
            .MapPost(
                "/repositories/{repositoryId}/pull-requests/{pullRequestNumber}/check-run/bypass",
                static (
                    string orgId,
                    string repositoryId,
                    int pullRequestNumber,
                    ClaimsPrincipal user,
                    [FromServices] BypassCheckRunHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, repositoryId, pullRequestNumber, user, ct)
            )
            .RequireActiveOrganization()
            .WithName("BypassCheckRun")
            .Produces<BypassCheckRunResponse>()
            .Produces<CodeReviewEndpointError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Bypass a blocking check run.")
            .WithDescription(
                """
                Clears a blocking Zeeq code review check run on the specified pull request.
                Any authenticated member of the organization (`orgId`) may bypass the check.

                Returns `200` with `cleared: true` when the check was cleared, `200` with
                `cleared: false` when no blocking check existed, or `404` when the PR
                cannot be found.
                """
            );
    }

    internal static bool TryDecodeSingleReviewToken(
        string? token,
        out DateTimeOffset createdAtUtc,
        out CodeReviewSingleViewMode mode,
        out CodeReviewEndpointError? error
    )
    {
        createdAtUtc = default;
        mode = default;

        if (string.IsNullOrWhiteSpace(token))
        {
            error = new("missing_token", "The review link token is required.");
            return false;
        }

        if (!CodeReviewSingleViewToken.TryDecode(token, out createdAtUtc, out mode))
        {
            error = new("invalid_token", "The review link token is malformed or unsupported.");
            return false;
        }

        error = null;
        return true;
    }
}
