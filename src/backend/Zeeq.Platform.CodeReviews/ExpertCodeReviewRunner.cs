using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using Zeeq.Core.Common;
using Zeeq.Core.Common.Storage;
using Zeeq.Core.Documents;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;
using Danom;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Synchronous MCP uploaded-diff review runner.
/// </summary>
public sealed partial class ExpertCodeReviewRunner(
    IStorageProvider<PostgresStorageWriteOptions> storage,
    CodeReviewDiffUploadTokenProtector tokenProtector,
    GitDiffParser diffParser,
    ICodeRepositoryStore repositories,
    CodeReviewerAgentResolver agentResolver,
    ICodeReviewAgentExecutor agentExecutor,
    CodeReviewXmlOutputValidator xmlValidator,
    ILibraryDocumentStore libraries,
    HybridCache cache,
    IOptions<AppSettings> appSettings,
    ICodeReviewRecordStore codeReviews,
    ICodeReviewArtifactStore artifacts,
    ICodeReviewPreviousReviewStore previousReviewStore,
    CodeReviewRequestLinkFactory linkFactory,
    ILogger<ExpertCodeReviewRunner> logger
) : IExpertCodeReviewRunner
{
    /// <inheritdoc />
    public Task<ExpertCodeReviewUploadUrlResponse> CreateUploadUrlAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    )
    {
        var authenticatedUser = user.AuthenticatedUser();
        if (authenticatedUser is null)
        {
            throw new InvalidOperationException(
                "An authenticated user is required to create an expert code-review upload URL."
            );
        }

        var organizationId = user.FindFirstValue(AuthClaims.OrganizationId);
        if (string.IsNullOrWhiteSpace(organizationId))
        {
            throw new InvalidOperationException(
                "The authenticated user token must include an organization id claim."
            );
        }

        var jobId = Guid.CreateVersion7().ToString("N");
        var expiresAtUtc = DateTimeOffset.UtcNow.Add(tokenProtector.GetValidity());
        var uploadToken = tokenProtector.Protect(
            CodeReviewDiffUploadTokenProtector.CreatePayload(
                jobId,
                expiresAtUtc,
                authenticatedUser.Sub,
                organizationId,
                ZeeqTelemetry.CaptureCurrentTraceContext()
            )
        );
        var uploadUrl = BuildUploadUrl(jobId, uploadToken);

        var response = new ExpertCodeReviewUploadUrlResponse(
            jobId,
            uploadToken,
            uploadUrl,
            expiresAtUtc,
            $"curl -X PUT --data-binary @/tmp/zeeq-review.diff \"{uploadUrl}\""
        );

        return Task.FromResult(response);
    }

    /// <inheritdoc />
    public async Task<Result<ExpertCodeReviewRunResponse, string>> RunReviewAsync(
        ExpertCodeReviewRunRequest request,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    )
    {
        if (
            !tokenProtector.TryUnprotect(
                request.UploadToken,
                out var payload,
                validateExpiry: false
            )
        )
        {
            return Result<ExpertCodeReviewRunResponse, string>.Error(
                "Invalid or unrecognized review token."
            );
        }

        var reviewGroupId = request.ReviewGroupId ?? $"crg_{Guid.CreateVersion7():N}";
        var previousReview = await codeReviews.FindNewestCompletedForReviewGroupAsync(
            payload!.OrganizationId,
            reviewGroupId,
            cancellationToken
        );

        var previousReviewLink =
            previousReview is { ExecutionTraceParent: { } traceParent }
            && ZeeqTelemetry.TryParseTraceContext(
                new(traceParent, previousReview.ExecutionTraceState),
                out var context
            )
                ? new ActivityLink(context)
                : (ActivityLink?)null;

        // A review is an independently discoverable workflow, not a child of the long-lived MCP
        // transport trace. TraceRoot starts a distinct trace and links it back to that transport
        // plus the latest completed review in this group, when its trace context is available.
        using var activity = ZeeqTelemetry.TraceRoot(
            [
                ("code_review.job_id", request.JobId),
                ("github.repo", request.OwnerQualifiedRepoName),
                ("code_review.review_group_id", reviewGroupId),
            ],
            "code-review.expert.run",
            previousReviewLink is { } link ? [link] : []
        );

        if (payload!.JobId != request.JobId)
        {
            return Result<ExpertCodeReviewRunResponse, string>.Error(
                "The review token does not match the requested job id."
            );
        }

        if (payload.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            return Result<ExpertCodeReviewRunResponse, string>.Error(
                "The review token has expired. Create a new upload URL and re-upload the diff."
            );
        }

        var path = UploadMcpCodeReviewDiffHandler.ToPath(request.JobId);
        try
        {
            var diffText = await ReadUploadedDiffAsync(path, cancellationToken);
            var files = diffParser.Parse(diffText).ToCodeReviewFileSnapshots();
            if (files.Count == 0)
            {
                return Result<ExpertCodeReviewRunResponse, string>.Error(
                    "Uploaded diff did not contain any parsable file sections."
                );
            }

            var repository = await repositories.FindActiveAsync(
                "github",
                request.OwnerQualifiedRepoName,
                cancellationToken
            );
            var reviewContext = await BuildReviewContextAsync(
                payload.OrganizationId,
                repository,
                request.OwnerQualifiedRepoName,
                files,
                cancellationToken
            );

            // Explicit caller-specified libraries are validated against the organization's library
            // list and, when at least one is valid, replace the repository's configured libraries
            // entirely — this is an explicit request for a specific set of libraries.
            var requestedLibraryNames = await libraries.ResolveExistingLibraryNamesAsync(
                reviewContext.OrganizationId,
                request.Libraries,
                cache,
                cancellationToken
            );

            var libraryNames = requestedLibraryNames.Length > 0
                ? requestedLibraryNames
                : await libraries.ResolveMappedLibraryNamesAsync(
                    reviewContext.OrganizationId,
                    repository?.LibraryIds,
                    cache,
                    cancellationToken
                );

            var prompt = CodeReviewUserPrompt.From(
                new(
                    request.Title ?? request.OwnerQualifiedRepoName,
                    request.Description ?? string.Empty,
                    [],
                    reviewContext.InScopeFiles,
                    reviewContext.OutOfScopeFiles,
                    libraryNames,
                    repository?.ReviewConfiguration.SharedPromptFragment ?? string.Empty
                )
            );

            // Previous-review context replaces the old `previousReviews: []`. Chains by
            // session id and/or group id (either). Group id is always present so chaining
            // works even when the agent supplied no session id.
            var previousReviews = await previousReviewStore.LoadForAgentAsync(
                payload.OrganizationId,
                request.AgentSessionId,
                reviewGroupId,
                excludeReviewId: string.Empty,
                cancellationToken: cancellationToken
            );

            // Per-run collector for the KB sources each reviewer consults; threaded to every
            // reviewer agent via the telemetry middleware and per-run options.
            var telemetry = new CodeReviewTelemetryContext(
                reviewContext.OrganizationId,
                repository?.Id
            );

            var xml = await agentExecutor.ExecuteAsync(
                reviewContext.OrganizationId,
                reviewContext.Agents,
                reviewContext.NoAgentsActivated,
                prompt,
                previousReviews,
                user,
                telemetry,
                cancellationToken
            );

            var validation = xmlValidator.Validate(xml);

            if (!validation.IsValid)
            {
                return Result<ExpertCodeReviewRunResponse, string>.Error(
                    "Code-review runner produced invalid XML: " + validation.ErrorMessage
                );
            }

            // Create the durable agent review record.
            var now = DateTimeOffset.UtcNow;
            var review = new CodeReviewRecord
            {
                Id = $"cr_{Guid.CreateVersion7():N}",
                OrganizationId = reviewContext.OrganizationId,
                TeamId = repository?.TeamId,
                PullRequestRecordId = null,
                RepositoryId = repository?.Id,
                OwnerQualifiedRepoName = request.OwnerQualifiedRepoName,
                PullRequestNumber = 0,
                Branch = request.Branch?.Trim() ?? string.Empty,
                Title = request.Title ?? request.OwnerQualifiedRepoName,
                AuthorLogin = user.AuthenticatedUser()?.Sub ?? string.Empty,
                Status = CodeReviewStatus.Completed,
                RequestOrigin = CodeReviewRequestOrigin.Agent,
                ReviewGroupId = reviewGroupId,
                AgentSessionId = request.AgentSessionId,
                PreviousReviewId = previousReview?.Id,
                ExecutionTraceParent = activity.Activity?.Id,
                ExecutionTraceState = activity.Activity?.TraceStateString,
                RemainingReviewBudget = 0,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };

            // Persist the record first so the artifact write has a durable row to reference.
            // If AddAsync fails, the whole operation fails and the caller gets an error — no
            // orphaned artifact because nothing was written yet.
            review = await codeReviews.AddAsync(review, cancellationToken);

            // Write findings artifact keyed by the persisted record.
            var counts = validation.Output!.CountFindings();
            await using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml)))
            {
                review.FindingsStorageUri = await artifacts.WriteFindingsAsync(
                    review,
                    stream,
                    "application/xml",
                    cancellationToken
                );
            }
            review.CriticalFindings = counts.Critical;
            review.MajorFindings = counts.Major;
            review.MinorFindings = counts.Minor;
            review.SuggestionFindings = counts.Suggestion;
            review.CommentFindings = counts.Comment;

            // Persist which KB sources the reviewers consulted. NOTE: unlike CodeReviewRunner, the
            // review row here is created only after a successful run (above), so there is no
            // pre-existing row to attach partial telemetry to when the agent run throws; capture is
            // therefore success-path only.
            review.SourceTelemetryPayload = telemetry.SerializeSnapshotPayload();

            // Update the persisted row with findings counts and storage URI.
            review = await codeReviews.UpdateAsync(review, cancellationToken);

            LogReviewPersisted(
                logger,
                review.OrganizationId,
                review.Id,
                review.AgentSessionId ?? "<none>",
                review.ReviewGroupId ?? "<none>"
            );

            // Build the deep link for the single-review view.
            var viewUrl = linkFactory.BuildSingleReviewLink(review, CodeReviewSingleViewMode.Agent);

            return Result<ExpertCodeReviewRunResponse, string>.Ok(
                new(
                    request.JobId,
                    xml,
                    [.. reviewContext.InScopeFiles.Select(file => file.Path)],
                    [.. reviewContext.OutOfScopeFiles.Select(file => file.Path)],
                    ReviewId: review.Id,
                    ReviewCreatedAtUtc: review.CreatedAtUtc,
                    ReviewGroupId: reviewGroupId,
                    ReviewViewUrl: viewUrl
                )
            );
        }
        catch (UploadedDiffNotFoundException)
        {
            return Result<ExpertCodeReviewRunResponse, string>.Error(
                "No uploaded diff found. Upload the diff before running the review."
            );
        }
        finally
        {
            await DeleteUploadedDiffAsync(path);
        }
    }

    private string BuildUploadUrl(string jobId, string uploadToken)
    {
        var baseUri = appSettings.Value.Http.ApiBaseUri.TrimEnd('/');

        return $"{baseUri}/api/v1/code-review/mcp-diffs/{jobId}?token={Uri.EscapeDataString(uploadToken)}";
    }

    private async Task<string> ReadUploadedDiffAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await storage.ReadTextAsync(
                path,
                StorageContainer.CodeReviewDiffs,
                cancellationToken
            );
        }
        catch (FileNotFoundException ex)
        {
            throw new UploadedDiffNotFoundException(path, ex);
        }
    }

    private async Task<UploadedDiffReviewContext> BuildReviewContextAsync(
        string fallbackOrganizationId,
        CodeRepository? repository,
        string ownerQualifiedRepoName,
        IReadOnlyList<CodeReviewFileSnapshot> files,
        CancellationToken cancellationToken
    )
    {
        if (repository is null)
        {
            LogRepositoryNotFound(
                logger,
                fallbackOrganizationId,
                ownerQualifiedRepoName,
                files.Count
            );
            return new(
                fallbackOrganizationId,
                [CodeReviewerAgentResolver.CreateDefaultRuntimeAgent()],
                NoAgentsActivated: false,
                InScopeFiles: files,
                OutOfScopeFiles: []
            );
        }

        var fileScope = CodeReviewFileFilterEvaluator.Apply(
            files,
            repository.ReviewConfiguration.FileFilter
        );
        var agentResolution = await agentResolver.ResolveAsync(
            repository.OrganizationId,
            repository.Id,
            fileScope.InScopeFiles,
            cancellationToken
        );

        return new(
            repository.OrganizationId,
            agentResolution.Agents,
            agentResolution.NoAgentsActivated,
            fileScope.InScopeFiles,
            fileScope.OutOfScopeFiles
        );
    }

    private async Task DeleteUploadedDiffAsync(string path)
    {
        try
        {
            await storage.DeleteAsync(
                path,
                StorageContainer.CodeReviewDiffs,
                CancellationToken.None
            );
        }
        catch (Exception ex)
        {
            LogDeleteUploadedDiffFailed(logger, path, ex);
        }
    }

    [LoggerMessage(
        EventId = 6101,
        Level = LogLevel.Warning,
        Message = "⚠️  Repository not found for MCP uploaded-diff review; falling back to default reviewer. OrganizationId={OrganizationId}, OwnerQualifiedRepoName={OwnerQualifiedRepoName}, FileCount={FileCount}"
    )]
    private static partial void LogRepositoryNotFound(
        ILogger logger,
        string organizationId,
        string ownerQualifiedRepoName,
        int fileCount
    );

    [LoggerMessage(
        EventId = 6100,
        Level = LogLevel.Warning,
        Message = "Failed to delete MCP uploaded diff {Path}."
    )]
    private static partial void LogDeleteUploadedDiffFailed(
        ILogger logger,
        string path,
        Exception exception
    );

    [LoggerMessage(
        EventId = 6102,
        Level = LogLevel.Information,
        Message = "Durable agent review record persisted. OrganizationId={OrganizationId}, ReviewId={ReviewId}, AgentSessionId={AgentSessionId}, ReviewGroupId={ReviewGroupId}"
    )]
    private static partial void LogReviewPersisted(
        ILogger logger,
        string organizationId,
        string reviewId,
        string agentSessionId,
        string reviewGroupId
    );

    private sealed record UploadedDiffReviewContext(
        string OrganizationId,
        IReadOnlyList<CodeReviewerRuntimeAgent> Agents,
        bool NoAgentsActivated,
        IReadOnlyList<CodeReviewFileSnapshot> InScopeFiles,
        IReadOnlyList<CodeReviewFileSnapshot> OutOfScopeFiles
    );

    private sealed class UploadedDiffNotFoundException(string path, Exception innerException)
        : FileNotFoundException("Uploaded diff was not found.", path, innerException);
}
