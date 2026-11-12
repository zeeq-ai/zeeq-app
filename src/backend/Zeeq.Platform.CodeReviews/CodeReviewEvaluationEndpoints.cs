using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Reviewer backtesting dataset, capture and revision endpoints.
/// </summary>
/// <remarks>
/// A dedicated endpoint class keeps the shared <see cref="CodeReviewEndpoints"/> route
/// registration untouched. Discovery is assembly-scanned, so no manual mapping is required.
/// Every route reuses the existing cookie authorization scheme and route/organization match
/// guard; management authorization is enforced per handler through
/// <see cref="CodeReviewAuthorization"/>.
/// </remarks>
public sealed class CodeReviewEvaluationEndpoints : IEndpoint
{
    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder app, IEndpointRouteBuilder rootApp)
    {
        var repositories = app.MapGroup("orgs/{orgId}/code-reviews/repositories/{repositoryId}")
            .WithTags("Code Review Evaluations")
            .RequireAuthorization(
                new Microsoft.AspNetCore.Authorization.AuthorizeAttribute
                {
                    AuthenticationSchemes = SetupIdentityExtension.CookieScheme,
                }
            )
            .RequireRouteOrganizationMatchesCookie();

        // GET /api/v1/orgs/{orgId}/code-reviews/repositories/{repositoryId}/evaluation-datasets
        repositories
            .MapGet(
                "/evaluation-datasets",
                static (
                    string orgId,
                    string repositoryId,
                    [FromQuery] bool? includeArchived,
                    ClaimsPrincipal user,
                    [FromServices] ListCodeReviewEvaluationDatasetsHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, repositoryId, includeArchived ?? false, user, ct)
            )
            .WithName("ListCodeReviewEvaluationDatasets")
            .Produces<CodeReviewEvaluationDatasetListResponse>()
            .Produces<CodeReviewEndpointError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("List evaluation datasets.")
            .WithDescription(
                """
                Returns the reviewer-backtesting datasets for one repository, most recently
                updated first. Archived datasets are excluded unless `includeArchived` is true.

                Restricted to organization owners and admins.
                """
            );

        // POST /api/v1/orgs/{orgId}/code-reviews/repositories/{repositoryId}/evaluation-datasets
        repositories
            .MapPost(
                "/evaluation-datasets",
                static (
                    string orgId,
                    string repositoryId,
                    [FromBody] CreateCodeReviewEvaluationDatasetRequest request,
                    ClaimsPrincipal user,
                    [FromServices] CreateCodeReviewEvaluationDatasetHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, repositoryId, request, user, ct)
            )
            .RequireActiveOrganization()
            .WithName("CreateCodeReviewEvaluationDataset")
            .Produces<CodeReviewEvaluationDatasetResponse>()
            .Produces<CodeReviewEndpointError>(StatusCodes.Status400BadRequest)
            .Produces<CodeReviewEndpointError>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces<CodeReviewEndpointError>(StatusCodes.Status503ServiceUnavailable)
            .WithSummary("Create an evaluation dataset.")
            .WithDescription(
                """
                Creates a persisted, named dataset so curation can start immediately. The call is
                idempotent on `createRequestKey`: a retry from the same repository returns the same
                dataset instead of creating a duplicate. Replaying the key from a different
                repository returns `409 create_request_key_conflict` and leaves the original
                dataset untouched. Add-PR resolution requires this persisted dataset id.
                """
            );

        // GET /api/v1/orgs/{orgId}/code-reviews/repositories/{repositoryId}/evaluation-datasets/{datasetId}
        repositories
            .MapGet(
                "/evaluation-datasets/{datasetId}",
                static (
                    string orgId,
                    string repositoryId,
                    string datasetId,
                    ClaimsPrincipal user,
                    [FromServices] GetCodeReviewEvaluationDatasetHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, repositoryId, datasetId, user, ct)
            )
            .WithName("GetCodeReviewEvaluationDataset")
            .Produces<CodeReviewEvaluationDatasetResponse>()
            .Produces<CodeReviewEndpointError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Get an evaluation dataset.")
            .WithDescription(
                """
                Returns one dataset with its captured cases and reference expectations. Capture
                identities are server-owned and immutable; labels are stored per reviewer agent.
                """
            );

        // PUT /api/v1/orgs/{orgId}/code-reviews/repositories/{repositoryId}/evaluation-datasets/{datasetId}
        repositories
            .MapPut(
                "/evaluation-datasets/{datasetId}",
                static (
                    string orgId,
                    string repositoryId,
                    string datasetId,
                    [FromBody] SaveCodeReviewEvaluationDatasetRequest? request,
                    ClaimsPrincipal user,
                    [FromServices] SaveCodeReviewEvaluationDatasetHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, repositoryId, datasetId, request, user, ct)
            )
            .RequireActiveOrganization()
            .WithName("SaveCodeReviewEvaluationDataset")
            .Produces<CodeReviewEvaluationDatasetResponse>()
            .Produces<CodeReviewEndpointError>(StatusCodes.Status400BadRequest)
            .Produces<CodeReviewEndpointError>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces<CodeReviewEndpointError>(StatusCodes.Status503ServiceUnavailable)
            .WithSummary("Save an evaluation dataset draft.")
            .WithDescription(
                """
                Applies one complete dataset draft under the expected version: membership,
                reference expectations and capture installs are installed together, and the
                version increments exactly once. A stale expected version returns `409` and
                nothing is written. Capture tokens are required for new membership and are
                rejected once expired or reused.
                """
            );

        // DELETE /api/v1/orgs/{orgId}/code-reviews/repositories/{repositoryId}/evaluation-datasets/{datasetId}
        repositories
            .MapDelete(
                "/evaluation-datasets/{datasetId}",
                static (
                    string orgId,
                    string repositoryId,
                    string datasetId,
                    ClaimsPrincipal user,
                    [FromServices] ArchiveCodeReviewEvaluationDatasetHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, repositoryId, datasetId, user, ct)
            )
            .RequireActiveOrganization()
            .WithName("ArchiveCodeReviewEvaluationDataset")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<CodeReviewEndpointError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces<CodeReviewEndpointError>(StatusCodes.Status503ServiceUnavailable)
            .WithSummary("Archive an evaluation dataset.")
            .WithDescription(
                """
                Archives the dataset. Existing runs, results and artifacts stay readable with
                their original identities; nothing is cascaded or deleted.
                """
            );

        // GET /api/v1/orgs/{orgId}/code-reviews/repositories/{repositoryId}/evaluation-pr-targets
        repositories
            .MapGet(
                "/evaluation-pr-targets",
                static (
                    string orgId,
                    string repositoryId,
                    [FromQuery] string? q,
                    [FromQuery] string? datasetId,
                    [FromQuery] string? cursor,
                    [FromQuery] int? pageSize,
                    ClaimsPrincipal user,
                    [FromServices] ListCodeReviewEvaluationPrTargetsHandler handler,
                    CancellationToken ct
                ) =>
                    handler.HandleAsync(
                        orgId,
                        repositoryId,
                        q,
                        datasetId,
                        cursor,
                        pageSize,
                        user,
                        ct
                    )
            )
            .WithName("ListCodeReviewEvaluationPrTargets")
            .Produces<CodeReviewEvaluationPrTargetListResponse>()
            .Produces<CodeReviewEndpointError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Browse pull requests for a dataset.")
            .WithDescription(
                """
                Returns known pull requests for the repository with server-side search and
                cursor pagination. Browsing never stages or mutates anything.
                """
            );

        // POST /api/v1/orgs/{orgId}/code-reviews/repositories/{repositoryId}/evaluation-pr-targets/resolve
        repositories
            .MapPost(
                "/evaluation-pr-targets/resolve",
                static (
                    string orgId,
                    string repositoryId,
                    [FromBody] ResolveCodeReviewEvaluationPrTargetsRequest? request,
                    ClaimsPrincipal user,
                    [FromServices] ResolveCodeReviewEvaluationPrTargetsHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, repositoryId, request, user, ct)
            )
            .RequireActiveOrganization()
            .WithName("ResolveCodeReviewEvaluationPrTargets")
            .Produces<ResolveCodeReviewEvaluationPrTargetsResponse>()
            .Produces<CodeReviewEndpointError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces<CodeReviewEndpointError>(StatusCodes.Status503ServiceUnavailable)
            .WithSummary("Resolve and stage pull request entries.")
            .WithDescription(
                """
                Resolves each manual entry independently and stages its snapshot capture. A
                failing entry reports its own error and never clears the other valid choices.
                Successful entries return an opaque capture token that Save requires.
                """
            );

        // GET /api/v1/orgs/{orgId}/code-reviews/repositories/{repositoryId}/evaluation-datasets/{datasetId}/cases/{caseId}/snapshot
        repositories
            .MapGet(
                "/evaluation-datasets/{datasetId}/cases/{caseId}/snapshot",
                static (
                    string orgId,
                    string repositoryId,
                    string datasetId,
                    string caseId,
                    ClaimsPrincipal user,
                    [FromServices] GetCodeReviewEvaluationCaseSnapshotHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, repositoryId, datasetId, caseId, user, ct)
            )
            .WithName("GetCodeReviewEvaluationCaseSnapshot")
            .Produces<CodeReviewEvaluationCaseSnapshotResponse>()
            .Produces<CodeReviewEndpointError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Get a saved case capture.")
            .WithDescription(
                """
                Returns the immutable snapshot captured for one dataset case, so a diff view can
                render stored code without a live provider call. The artifact path comes from the
                server-side case row; callers never supply a storage URI. Available immediately
                after capture, before any evaluation run.

                Restricted to organization owners and admins.
                """
            );

        // POST /api/v1/orgs/{orgId}/code-reviews/repositories/{repositoryId}/evaluation-datasets/{datasetId}/cases/{caseId}/refresh
        repositories
            .MapPost(
                "/evaluation-datasets/{datasetId}/cases/{caseId}/refresh",
                static (
                    string orgId,
                    string repositoryId,
                    string datasetId,
                    string caseId,
                    ClaimsPrincipal user,
                    [FromServices] RefreshCodeReviewEvaluationCaseHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, repositoryId, datasetId, caseId, user, ct)
            )
            .RequireActiveOrganization()
            .WithName("RefreshCodeReviewEvaluationCase")
            .Produces<RefreshCodeReviewEvaluationCaseResponse>()
            .Produces<CodeReviewEndpointError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces<CodeReviewEndpointError>(StatusCodes.Status503ServiceUnavailable)
            .WithSummary("Stage a fresh capture for one case.")
            .WithDescription(
                """
                Captures the pull request again and stages it without persisting anything. A
                later Save with the returned token keeps the case id and resets its reference
                expectations to unlabelled, because the code changed.
                """
            );

        var reviews = app.MapGroup("orgs/{orgId}/code-reviews")
            .WithTags("Code Review Evaluations")
            .RequireAuthorization(
                new Microsoft.AspNetCore.Authorization.AuthorizeAttribute
                {
                    AuthenticationSchemes = SetupIdentityExtension.CookieScheme,
                }
            )
            .RequireRouteOrganizationMatchesCookie();

        // GET /api/v1/orgs/{orgId}/code-reviews/agents/{agentId}/revisions
        reviews
            .MapGet(
                "/agents/{agentId}/revisions",
                static (
                    string orgId,
                    string agentId,
                    [FromQuery] int? pageSize,
                    ClaimsPrincipal user,
                    [FromServices] ListCodeReviewerAgentRevisionsHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, agentId, pageSize, user, ct)
            )
            .WithName("ListCodeReviewerAgentRevisions")
            .Produces<CodeReviewerAgentRevisionListResponse>()
            .Produces<CodeReviewEndpointError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("List reviewer revisions.")
            .WithDescription(
                """
                Returns up to the latest 200 saved reviewer configurations, newest first.
                This v1 picker has no continuation cursor; existing reports retain older tested
                content. An agent without revisions gets an initial snapshot on first view;
                no batch backfill runs.
                """
            );

        // GET /api/v1/orgs/{orgId}/code-reviews/agents/{agentId}/revisions/{revisionId}
        reviews
            .MapGet(
                "/agents/{agentId}/revisions/{revisionId}",
                static (
                    string orgId,
                    string agentId,
                    string revisionId,
                    ClaimsPrincipal user,
                    [FromServices] GetCodeReviewerAgentRevisionHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, agentId, revisionId, user, ct)
            )
            .WithName("GetCodeReviewerAgentRevision")
            .Produces<CodeReviewerAgentRevisionResponse>()
            .Produces<CodeReviewEndpointError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Get a reviewer revision.")
            .WithDescription(
                """
                Returns one immutable revision's full configuration for the read-only diff
                view. Selecting a revision never restores or deploys it.
                """
            );
    }
}
