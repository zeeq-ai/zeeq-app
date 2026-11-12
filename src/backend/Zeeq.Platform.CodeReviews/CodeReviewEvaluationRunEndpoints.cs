using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Zeeq.Core.Common;
using Zeeq.Core.Models;
using Zeeq.Platform.Messaging;

namespace Zeeq.Platform.CodeReviews;

/// <summary>Request to evaluate one reviewer configuration against a dataset.</summary>
public sealed record CreateEvaluationRunRequest(
    string DatasetId,
    long ExpectedVersion,
    string AgentId,
    string? SavedRevisionId,
    CodeReviewerAgentRevisionConfiguration? Draft,
    string RequestKey
);

/// <summary>Idempotency key for a failed-case retry.</summary>
public sealed record RetryEvaluationRunRequest(string RequestKey);

/// <summary>Public run summary; does not expose lease, artifact paths or credential references.</summary>
public sealed record EvaluationRunDto(
    string Id,
    string DatasetId,
    long DatasetVersion,
    string AgentId,
    string? SavedRevisionId,
    string? ParentRunId,
    CodeReviewEvaluationRunState State,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    CodeReviewEvaluationRunCounts Counts,
    string? ErrorCode
);

/// <summary>A sanitized case result and immutable reference content.</summary>
public sealed record EvaluationRunCaseDto(
    string CaseId,
    int PullRequestNumber,
    string Title,
    string BaseSha,
    string HeadSha,
    int OmittedFileCount,
    CodeReviewEvaluationReferenceSet References,
    CodeReviewEvaluationCasePhase Phase,
    CodeReviewEvaluationCaseOutcome Outcome,
    IReadOnlyList<CodeReviewEvaluationCandidate> CandidateFindings,
    IReadOnlyList<CodeReviewEvaluationMatch> JudgeMatches,
    string? ReviewerProvenanceJson,
    string? JudgeProvenanceJson,
    string? ErrorCode
);

/// <summary>Run details with immutable case results.</summary>
public sealed record EvaluationRunDetailDto(
    EvaluationRunDto Run,
    IReadOnlyList<EvaluationRunCaseDto> Cases
);

/// <summary>Page of evaluation runs.</summary>
public sealed record EvaluationRunListDto(
    IReadOnlyList<EvaluationRunDto> Items,
    string? NextCursorId
);

/// <summary>Active-run or stale-version conflict.</summary>
public sealed record EvaluationRunConflictDto(string Code, string? ActiveRunId);

/// <summary>Durable evaluation run routes.</summary>
public sealed class CodeReviewEvaluationRunEndpoints : IEndpoint
{
    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder app, IEndpointRouteBuilder rootApp)
    {
        var group = app.MapGroup("orgs/{orgId}/code-reviews/repositories/{repoId}/evaluation-runs")
            .WithTags("Code Review Evaluations")
            .RequireAuthorization(
                new AuthorizeAttribute
                {
                    AuthenticationSchemes = SetupIdentityExtension.CookieScheme,
                }
            )
            .RequireRouteOrganizationMatchesCookie();
        group
            .MapPost(
                "/",
                (
                    string orgId,
                    string repoId,
                    CreateEvaluationRunRequest request,
                    ClaimsPrincipal user,
                    EvaluationRunHandler handler,
                    CancellationToken ct
                ) => handler.CreateAsync(orgId, repoId, request, user, ct)
            )
            .WithName("CreateCodeReviewEvaluationRun")
            .Produces<EvaluationRunDto>(202)
            .Produces<EvaluationRunConflictDto>(409)
            .Produces<CodeReviewEndpointError>(400)
            .Produces(403)
            .Produces(404)
            .Produces(503);
        group
            .MapGet(
                "/",
                (
                    string orgId,
                    string repoId,
                    string? datasetId,
                    string? cursorId,
                    int? pageSize,
                    ClaimsPrincipal user,
                    EvaluationRunHandler handler,
                    CancellationToken ct
                ) => handler.ListAsync(orgId, repoId, datasetId, cursorId, pageSize, user, ct)
            )
            .WithName("ListCodeReviewEvaluationRuns")
            .Produces<EvaluationRunListDto>()
            .Produces(403)
            .Produces(404);
        group
            .MapGet(
                "/{runId}",
                (
                    string orgId,
                    string repoId,
                    string runId,
                    ClaimsPrincipal user,
                    EvaluationRunHandler handler,
                    CancellationToken ct
                ) => handler.GetAsync(orgId, repoId, runId, user, ct)
            )
            .WithName("GetCodeReviewEvaluationRun")
            .Produces<EvaluationRunDetailDto>()
            .Produces(403)
            .Produces(404);
        group
            .MapPost(
                "/{runId}/cancel",
                (
                    string orgId,
                    string repoId,
                    string runId,
                    ClaimsPrincipal user,
                    EvaluationRunHandler handler,
                    CancellationToken ct
                ) => handler.MutateAsync(orgId, repoId, runId, "cancel", null, user, ct)
            )
            .WithName("CancelCodeReviewEvaluationRun")
            .Produces<EvaluationRunDto>()
            .Produces(403)
            .Produces(404)
            .Produces(409);
        group
            .MapPost(
                "/{runId}/abandon",
                (
                    string orgId,
                    string repoId,
                    string runId,
                    ClaimsPrincipal user,
                    EvaluationRunHandler handler,
                    CancellationToken ct
                ) => handler.MutateAsync(orgId, repoId, runId, "abandon", null, user, ct)
            )
            .WithName("AbandonCodeReviewEvaluationRun")
            .Produces<EvaluationRunDto>()
            .Produces(403)
            .Produces(404)
            .Produces(409);
        group
            .MapPost(
                "/{runId}/retry-failed",
                (
                    string orgId,
                    string repoId,
                    string runId,
                    RetryEvaluationRunRequest request,
                    ClaimsPrincipal user,
                    EvaluationRunHandler handler,
                    CancellationToken ct
                ) => handler.MutateAsync(orgId, repoId, runId, "retry", request, user, ct)
            )
            .WithName("RetryCodeReviewEvaluationRun")
            .Produces<EvaluationRunDto>(202)
            .Produces<EvaluationRunConflictDto>(409)
            .Produces(400)
            .Produces(403)
            .Produces(404)
            .Produces(503);
        group
            .MapGet(
                "/{runId}/cases/{caseId}/snapshot",
                (
                    string orgId,
                    string repoId,
                    string runId,
                    string caseId,
                    ClaimsPrincipal user,
                    EvaluationRunHandler handler,
                    CancellationToken ct
                ) => handler.SnapshotAsync(orgId, repoId, runId, caseId, user, ct)
            )
            .WithName("GetCodeReviewEvaluationSnapshot")
            .Produces<CodeReviewEvaluationSnapshot>()
            .Produces(403)
            .Produces(404);
        group
            .MapGet(
                "/{runId}/cases/{caseId}/candidate",
                (
                    string orgId,
                    string repoId,
                    string runId,
                    string caseId,
                    ClaimsPrincipal user,
                    EvaluationRunHandler handler,
                    CancellationToken ct
                ) => handler.CandidateAsync(orgId, repoId, runId, caseId, user, ct)
            )
            .WithName("GetCodeReviewEvaluationCandidate")
            .Produces<string>(200, "application/xml")
            .Produces(403)
            .Produces(404);
    }
}

/// <summary>Coordinates authorization, admission and durable run reads.</summary>
public sealed class EvaluationRunHandler(
    CodeReviewAuthorization authorization,
    ICodeRepositoryStore repositories,
    ICodeReviewerAgentStore agents,
    ICodeReviewerAgentRevisionStore revisions,
    ICodeReviewEvaluationRunStore runs,
    ICodeReviewEvaluationCaseResultStore results,
    ICodeReviewEvaluationArtifactStore artifacts,
    CodeReviewLlmTierResolver resolver,
    IZeeqMessagePublisher publisher,
    CodeReviewSettings settings
) : IEndpointHandler
{
    private const int MaxRequestKeyLength = 128;
    private static string BuildVersion =>
        typeof(EvaluationRunHandler)
            .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? "unavailable";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
    private static string PromptHash =>
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(CodeReviewSystemPrompt.CommonInstructions))
        );

    private static string Fingerprint(object value) =>
        Convert.ToHexStringLower(
            SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions))
        );

    private static string CreateFingerprint(CreateEvaluationRunRequest request) =>
        Fingerprint(
            new
            {
                request.DatasetId,
                request.ExpectedVersion,
                request.AgentId,
                request.SavedRevisionId,
                request.Draft,
            }
        );

    private static string RetryFingerprint(string runId) =>
        Fingerprint(new { ParentRunId = runId });

    private static EvaluationRunDto Map(CodeReviewEvaluationRun r) =>
        new(
            r.Id,
            r.DatasetId,
            r.DatasetVersion,
            r.AgentId,
            r.SavedRevisionId,
            r.ParentRunId,
            r.State,
            r.CreatedAtUtc,
            r.StartedAtUtc,
            r.CompletedAtUtc,
            new(
                r.CaseCount,
                r.GradedCaseCount,
                r.SkippedCaseCount,
                r.FailedCaseCount,
                r.UnlabelledCaseCount,
                r.MatchedReferenceCount,
                r.MissedReferenceCount,
                r.ExpectedReferenceCount,
                r.AdditionalFindingCount
            ),
            r.ErrorCode
        );

    private async Task<(CodeReviewAccess? Access, IResult? Error)> AccessAsync(
        string org,
        string repo,
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        var access = await authorization.ResolveAsync(org, user, ct);
        if (access is null)
            return (null, TypedResults.NotFound());
        if (!access.CanManage)
            return (null, TypedResults.Forbid());
        if (await repositories.FindActiveForOrganizationAsync(org, repo, ct) is null)
            return (null, TypedResults.NotFound());
        return (access, null);
    }

    /// <summary>Captures settings and atomically admits a new run before publishing it.</summary>
    public async Task<IResult> CreateAsync(
        string org,
        string repo,
        CreateEvaluationRunRequest request,
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        var (access, error) = await AccessAsync(org, repo, user, ct);
        if (error is not null)
            return error;
        if (!settings.EvaluationsEnabled)
            return TypedResults.Json(
                new CodeReviewEndpointError("feature_disabled", "Evaluations are disabled."),
                statusCode: 503
            );
        if (
            request is null
            || string.IsNullOrWhiteSpace(request.RequestKey)
            || request.RequestKey.Length > MaxRequestKeyLength
            || string.IsNullOrWhiteSpace(request.DatasetId)
            || string.IsNullOrWhiteSpace(request.AgentId)
            || (request.SavedRevisionId is not null && request.Draft is not null)
        )
            return TypedResults.BadRequest(
                new CodeReviewEndpointError(
                    "invalid_request",
                    "Dataset, agent and request key (at most 128 characters) are required; choose a revision or draft."
                )
            );
        var existing = await runs.FindByRequestKeyAsync(org, request.RequestKey, ct);
        if (existing is not null)
            return MatchesCreateRequest(existing, repo, request)
                ? TypedResults.Json(Map(existing.Run), statusCode: StatusCodes.Status202Accepted)
                : TypedResults.Conflict(new EvaluationRunConflictDto("request_key_conflict", null));
        var agent = await agents.FindAsync(org, request.AgentId, ct);
        if (agent is null || agent.RepositoryId != repo)
            return TypedResults.NotFound();
        var repository = await repositories.FindActiveForOrganizationAsync(org, repo, ct);
        if (repository is null)
            return TypedResults.NotFound();
        CodeReviewerAgentRevisionConfiguration config;
        if (request.SavedRevisionId is not null)
        {
            var revision = await revisions.FindAsync(org, agent.Id, request.SavedRevisionId, ct);
            if (revision is null || revision.RepositoryId != repo)
                return TypedResults.NotFound();
            config = revision.Configuration;
        }
        else if (request.Draft is not null)
            config = request.Draft;
        else
        {
            var revision = await revisions.EnsureInitialSnapshotAsync(
                agent,
                access!.UserId,
                DateTimeOffset.UtcNow,
                ct
            );
            config = revision.Configuration;
        }
        if (
            string.IsNullOrWhiteSpace(config.DisplayName)
            || config.DisplayName.Length > 256
            || string.IsNullOrWhiteSpace(config.ReviewFacet)
            || config.ReviewFacet.Length > 128
            || string.IsNullOrWhiteSpace(config.Prompt)
            || !Enum.IsDefined(config.ModelTier)
            || config.ActivationConfiguration is null
        )
            return TypedResults.BadRequest(
                new CodeReviewEndpointError("invalid_agent", "Reviewer configuration is invalid.")
            );
        var activationError = CodeReviewerAgentEndpointValidation.ValidateActivationConfiguration(
            CodeReviewEndpointMapping.ToDto(config.ActivationConfiguration)
        );
        if (activationError is not null)
            return TypedResults.BadRequest(activationError);
        var reviewerModel = await resolver.CaptureAsync(org, config.ModelTier, ct);
        var judgeModel = await resolver.CaptureAsync(org, CodeReviewModelTier.High, ct);
        var admitted = await runs.AdmitAsync(
            new(
                org,
                repo,
                request.DatasetId,
                request.ExpectedVersion,
                agent.Id,
                request.SavedRevisionId,
                config,
                reviewerModel,
                judgeModel,
                CodeReviewEvaluationJudge.RubricVersion,
                CodeReviewSystemPrompt.CommonInstructions,
                PromptHash,
                JsonSerializer.Serialize(repository.ReviewConfiguration),
                repository.LibraryIds ?? [],
                BuildVersion,
                PromptHash,
                access!.UserId,
                request.RequestKey,
                CreateFingerprint(request)
            ),
            ct
        );
        if (
            admitted.Outcome == CodeReviewEvaluationRunAdmissionOutcome.ExistingRequestKey
            && admitted.Run is { } existingRun
        )
        {
            var raced = await runs.FindAsync(org, existingRun.Id, ct);
            if (raced is null || !MatchesCreateRequest(raced, repo, request))
                return TypedResults.Conflict(
                    new EvaluationRunConflictDto("request_key_conflict", null)
                );
        }
        return await AdmissionResponseAsync(
            org,
            repo,
            repository.TeamId,
            admitted,
            request.DatasetId,
            agent.Id,
            null,
            ct
        );
    }

    /// <summary>Lists runs under an authorized repository.</summary>
    public async Task<IResult> ListAsync(
        string org,
        string repo,
        string? datasetId,
        string? cursorId,
        int? pageSize,
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        var (_, error) = await AccessAsync(org, repo, user, ct);
        if (error is not null)
            return error;
        var page = await runs.ListAsync(
            new(org, repo, datasetId, cursorId, Math.Clamp(pageSize ?? 50, 1, 100)),
            ct
        );
        return TypedResults.Ok(
            new EvaluationRunListDto([.. page.Items.Select(Map)], page.NextCursorId)
        );
    }

    private async Task<CodeReviewEvaluationRunDetail?> FindAsync(
        string org,
        string repo,
        string runId,
        CancellationToken ct
    )
    {
        var detail = await runs.FindAsync(org, runId, ct);
        return detail?.Run.RepositoryId == repo ? detail : null;
    }

    /// <summary>Reads a run and results without exposing private manifest fields.</summary>
    public async Task<IResult> GetAsync(
        string org,
        string repo,
        string runId,
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        var (_, error) = await AccessAsync(org, repo, user, ct);
        if (error is not null)
            return error;
        var detail = await FindAsync(org, repo, runId, ct);
        if (detail is null)
            return TypedResults.NotFound();
        // NOTE: A manifest contains at most 25 cases. The report needs candidate findings,
        // matches and provenance together; keep this bounded read rather than introducing
        // a second persistence contract solely to omit a few scalar columns.
        var rows = (await results.ListForRunAsync(org, runId, ct)).ToDictionary(x =>
            x.ManifestCaseId
        );
        var cases = detail
            .Manifest.Cases.Select(x =>
            {
                rows.TryGetValue(x.CaseId, out var row);
                return new EvaluationRunCaseDto(
                    x.CaseId,
                    x.PullRequestNumber,
                    x.Title,
                    x.BaseSha,
                    x.HeadSha,
                    x.OmittedFileCount,
                    x.References,
                    row?.Phase ?? CodeReviewEvaluationCasePhase.Pending,
                    row?.Outcome ?? CodeReviewEvaluationCaseOutcome.None,
                    DeserializeList<CodeReviewEvaluationCandidate>(row?.CandidateFindingsJson),
                    DeserializeList<CodeReviewEvaluationMatch>(row?.JudgeMatchesJson),
                    PublicProvenance(row?.ReviewerProvenanceJson),
                    PublicProvenance(row?.JudgeProvenanceJson),
                    row?.ErrorCode
                );
            })
            .ToArray();
        return TypedResults.Ok(new EvaluationRunDetailDto(Map(detail.Run), cases));
    }

    private static IReadOnlyList<T> DeserializeList<T>(string? json) =>
        json is null ? [] : JsonSerializer.Deserialize<List<T>>(json, JsonOptions) ?? [];

    private static string? PublicProvenance(string? json)
    {
        if (json is null)
            return null;
        var value = JsonNode.Parse(json)?.AsObject();
        value?.Remove("actualSystemPrompt");
        value?.Remove("sourceTelemetry");
        return value?.ToJsonString();
    }

    private static bool MatchesCreateRequest(
        CodeReviewEvaluationRunDetail existing,
        string repositoryId,
        CreateEvaluationRunRequest request
    ) =>
        existing.Run.RepositoryId == repositoryId
        && existing.Run.DatasetId == request.DatasetId
        && existing.Run.DatasetVersion == request.ExpectedVersion
        && existing.Run.AgentId == request.AgentId
        && existing.Run.ParentRunId is null
        && existing.Run.SavedRevisionId == request.SavedRevisionId
        && existing.Manifest.RequestFingerprint == CreateFingerprint(request);

    /// <summary>Applies cancellation, explicit abandonment or failed-case retry.</summary>
    public async Task<IResult> MutateAsync(
        string org,
        string repo,
        string runId,
        string action,
        RetryEvaluationRunRequest? request,
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        var (access, error) = await AccessAsync(org, repo, user, ct);
        if (error is not null)
            return error;
        var detail = await FindAsync(org, repo, runId, ct);
        if (detail is null)
            return TypedResults.NotFound();
        if (action == "cancel")
            return TypedResults.Ok(Map(await runs.RequestCancelAsync(org, runId, ct)));
        if (action == "abandon")
        {
            try
            {
                return TypedResults.Ok(
                    Map(await runs.AbandonAsync(org, runId, "ExplicitAbandon", ct))
                );
            }
            catch (CodeReviewEvaluationFencedException ex) when (ex.ReasonCode == "lease_active")
            {
                return TypedResults.Conflict(
                    new CodeReviewEndpointError(
                        "lease_active",
                        "Cancel the run or wait for its lease to expire before abandoning it."
                    )
                );
            }
        }
        if (action != "retry")
            throw new ArgumentOutOfRangeException(nameof(action));
        if (!settings.EvaluationsEnabled)
            return TypedResults.Json(
                new CodeReviewEndpointError("feature_disabled", "Evaluations are disabled."),
                statusCode: 503
            );
        if (
            request is null
            || string.IsNullOrWhiteSpace(request.RequestKey)
            || request.RequestKey.Length > MaxRequestKeyLength
        )
            return TypedResults.BadRequest(
                new CodeReviewEndpointError(
                    "invalid_request",
                    "Request key is required and must be at most 128 characters."
                )
            );
        var existing = await runs.FindByRequestKeyAsync(org, request.RequestKey, ct);
        if (existing is not null)
            return
                existing.Run.RepositoryId == repo
                && existing.Run.ParentRunId == runId
                && existing.Manifest.RequestFingerprint == RetryFingerprint(runId)
                ? TypedResults.Json(Map(existing.Run), statusCode: StatusCodes.Status202Accepted)
                : TypedResults.Conflict(new EvaluationRunConflictDto("request_key_conflict", null));
        var admitted = await runs.AdmitRetryAsync(
            new(
                org,
                runId,
                request.RequestKey,
                access!.UserId,
                BuildVersion,
                PromptHash,
                RetryFingerprint(runId)
            ),
            ct
        );
        if (
            admitted.Outcome == CodeReviewEvaluationRunAdmissionOutcome.ExistingRequestKey
            && admitted.Run is { } existingRun
        )
        {
            var raced = await runs.FindAsync(org, existingRun.Id, ct);
            if (
                raced is null
                || raced.Run.RepositoryId != repo
                || raced.Run.ParentRunId != runId
                || raced.Manifest.RequestFingerprint != RetryFingerprint(runId)
            )
                return TypedResults.Conflict(
                    new EvaluationRunConflictDto("request_key_conflict", null)
                );
        }
        return await AdmissionResponseAsync(
            org,
            repo,
            detail.Run.TeamId,
            admitted,
            detail.Run.DatasetId,
            detail.Run.AgentId,
            runId,
            ct
        );
    }

    /// <summary>Reads the immutable, verified snapshot after parent authorization.</summary>
    public async Task<IResult> SnapshotAsync(
        string org,
        string repo,
        string runId,
        string caseId,
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        var (_, error) = await AccessAsync(org, repo, user, ct);
        if (error is not null)
            return error;
        var detail = await FindAsync(org, repo, runId, ct);
        var item = detail?.Manifest.Cases.FirstOrDefault(x => x.CaseId == caseId);
        if (item is null)
            return TypedResults.NotFound();
        return TypedResults.Ok(await artifacts.ReadSnapshotAsync(org, repo, item.Snapshot, ct));
    }

    /// <summary>Reads verified candidate XML through its authorized run and case.</summary>
    public async Task<IResult> CandidateAsync(
        string org,
        string repo,
        string runId,
        string caseId,
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        var (_, error) = await AccessAsync(org, repo, user, ct);
        if (error is not null)
            return error;
        var detail = await FindAsync(org, repo, runId, ct);
        if (detail is null || !detail.Manifest.Cases.Any(x => x.CaseId == caseId))
            return TypedResults.NotFound();
        var row = await results.FindAsync(org, runId, caseId, ct);
        if (
            row?.CandidateStorageUri is null
            || row.CandidateSha256 is null
            || row.CandidateByteCount is null
        )
            return TypedResults.NotFound();
        var xml = await artifacts.ReadCandidateOutputAsync(
            org,
            repo,
            new(row.CandidateStorageUri, row.CandidateSha256, row.CandidateByteCount.Value),
            ct
        );
        return TypedResults.Content(xml, "application/xml");
    }

    private async Task<IResult> AdmissionResponseAsync(
        string org,
        string repo,
        string? team,
        CodeReviewEvaluationRunAdmissionResult admitted,
        string expectedDataset,
        string expectedAgent,
        string? expectedParent,
        CancellationToken ct
    )
    {
        if (admitted.Outcome == CodeReviewEvaluationRunAdmissionOutcome.DatasetNotFound)
            return TypedResults.NotFound();
        if (
            admitted.Outcome
            is not (
                CodeReviewEvaluationRunAdmissionOutcome.Admitted
                or CodeReviewEvaluationRunAdmissionOutcome.ExistingRequestKey
            )
        )
            return TypedResults.Conflict(
                new EvaluationRunConflictDto(admitted.Outcome.ToString(), admitted.ActiveRunId)
            );
        var run = admitted.Run!;
        if (
            run.RepositoryId != repo
            || run.DatasetId != expectedDataset
            || run.AgentId != expectedAgent
            || run.ParentRunId != expectedParent
        )
            return TypedResults.Conflict(
                new EvaluationRunConflictDto("request_key_conflict", null)
            );
        if (admitted.Outcome == CodeReviewEvaluationRunAdmissionOutcome.Admitted)
        {
            try
            {
                await publisher.PublishAsync(
                    new CodeReviewEvaluationRunRequested
                    {
                        OrganizationId = org,
                        TeamId = team,
                        RunId = run.Id,
                    },
                    ct
                );
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                run = await runs.MarkDispatchFailedAsync(org, run.Id, "DispatchFailed", ct);
                return TypedResults.Json(
                    new CodeReviewEndpointError(
                        "dispatch_failed",
                        "Run dispatch failed; check run history."
                    ),
                    statusCode: 503
                );
            }
        }
        return TypedResults.Json(Map(run), statusCode: StatusCodes.Status202Accepted);
    }
}
