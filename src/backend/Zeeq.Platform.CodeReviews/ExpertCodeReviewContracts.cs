using System.Security.Claims;
using Danom;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Runs the MCP-facing expert code-review flow.
/// </summary>
public interface IExpertCodeReviewRunner
{
    /// <summary>
    /// Creates an upload URL and encrypted token for one local diff review job.
    /// </summary>
    Task<ExpertCodeReviewUploadUrlResponse> CreateUploadUrlAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Runs the expert reviewers over a previously uploaded diff.
    /// </summary>
    Task<Result<ExpertCodeReviewRunResponse, string>> RunReviewAsync(
        ExpertCodeReviewRunRequest request,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// Upload URL metadata returned to the MCP caller.
/// </summary>
/// <param name="JobId">Uploaded-diff job id.</param>
/// <param name="UploadToken">Encrypted token authorizing upload and review execution.</param>
/// <param name="UploadUrl">Absolute HTTP URL that accepts the raw diff body.</param>
/// <param name="ExpiresAtUtc">Hard UTC expiry for the upload and review run.</param>
/// <param name="CurlExample">Ready-to-run curl example using <c>--data-binary</c>.</param>
public sealed record ExpertCodeReviewUploadUrlResponse(
    string JobId,
    string UploadToken,
    string UploadUrl,
    DateTimeOffset ExpiresAtUtc,
    string CurlExample
)
{
    /// <summary>
    /// Renders the upload instructions as tool text for local coding agents.
    /// </summary>
    public string RenderAsText(int maxDiffSizeBytes) =>
        $$"""
            Upload URL created for expert code review.

            jobId: {{JobId}}
            uploadToken: {{UploadToken}}
            expiresAtUtc: {{ExpiresAtUtc:O}}
            maxDiffSizeBytes: {{maxDiffSizeBytes}}

            Upload the raw unified git diff with:
            {{CurlExample}}

            Use git or gh to resolve the remote origin for the repository name (owning-org/repo-name)

            Then call `expert_code_review` with action=`run_review`, this `jobId`, this `uploadToken`, `ownerQualifiedRepoName`, and review context.
            """;
}

/// <summary>
/// Raw MCP tool inputs for a review run before validation.
/// </summary>
/// <param name="JobId">Job id returned by <c>create_upload_url</c>.</param>
/// <param name="UploadToken">Upload token returned by <c>create_upload_url</c>.</param>
/// <param name="OwnerQualifiedRepoName">Provider repository identity, such as <c>owner/repo</c>.</param>
/// <param name="Title">Optional review title.</param>
/// <param name="Description">Optional review description.</param>
/// <param name="AgentSessionId">
/// Optional; stable coding-agent session id so repeat reviews in one session share prior-finding
/// context and can be linked later.
/// </param>
/// <param name="ReviewGroupId">
/// Optional; review group id returned by a prior <c>expert_code_review</c> call to chain related
/// reviews. Minted when absent.
/// </param>
/// <param name="Branch">Optional active or planned git branch for PR-workstream association.</param>
public sealed record ExpertCodeReviewRunInput(
    string? JobId,
    string? UploadToken,
    string? OwnerQualifiedRepoName,
    string? Title,
    string? Description,
    string? AgentSessionId = null,
    string? ReviewGroupId = null,
    string? Branch = null
)
{
    /// <summary>
    /// Validates required run-review inputs.
    /// </summary>
    public Result<ExpertCodeReviewRunRequest, string> ToRequest()
    {
        if (string.IsNullOrWhiteSpace(JobId))
        {
            return Result<ExpertCodeReviewRunRequest, string>.Error(
                "jobId is required for run_review."
            );
        }

        if (string.IsNullOrWhiteSpace(UploadToken))
        {
            return Result<ExpertCodeReviewRunRequest, string>.Error(
                "uploadToken is required for run_review."
            );
        }

        if (string.IsNullOrWhiteSpace(OwnerQualifiedRepoName))
        {
            return Result<ExpertCodeReviewRunRequest, string>.Error(
                "remoteRepoNameUsingGit is required for run_review (use `git remote -v` or gh to resolve the remote in the format owning-org/repo-name)."
            );
        }

        return Result<ExpertCodeReviewRunRequest, string>.Ok(
            new(
                JobId.Trim(),
                UploadToken.Trim(),
                OwnerQualifiedRepoName.Trim(),
                Title,
                Description,
                AgentSessionId,
                ReviewGroupId,
                Branch
            )
        );
    }
}

/// <summary>
/// Validated request for running an MCP uploaded-diff review.
/// </summary>
/// <param name="JobId">Job id returned by <c>create_upload_url</c>.</param>
/// <param name="UploadToken">Upload token returned by <c>create_upload_url</c>.</param>
/// <param name="OwnerQualifiedRepoName">Provider repository identity, such as <c>owner/repo</c>.</param>
/// <param name="Title">Optional review title.</param>
/// <param name="Description">Optional review description.</param>
/// <param name="AgentSessionId">Optional stable coding-agent session id for chaining reviews.</param>
/// <param name="ReviewGroupId">Optional review group id for chaining reviews; minted when absent.</param>
/// <param name="Branch">Optional active or planned git branch for PR-workstream association.</param>
public sealed record ExpertCodeReviewRunRequest(
    string JobId,
    string UploadToken,
    string OwnerQualifiedRepoName,
    string? Title,
    string? Description,
    string? AgentSessionId,
    string? ReviewGroupId,
    string? Branch
);

/// <summary>
/// Review XML and file-scope metadata returned to the MCP caller.
/// </summary>
/// <param name="JobId">Uploaded-diff job id.</param>
/// <param name="ReviewXml">Canonical <c>&lt;reviews&gt;</c> XML returned by the reviewer agents.</param>
/// <param name="ReviewedFiles">Files included in reviewer prompt patches.</param>
/// <param name="OutOfScopeFiles">Files excluded by repository filters.</param>
/// <param name="ReviewId">Durable review record ID for the persisted agent review.</param>
/// <param name="ReviewCreatedAtUtc">Partition timestamp of the persisted review row.</param>
/// <param name="ReviewGroupId">Group id for chaining follow-up reviews.</param>
/// <param name="ReviewViewUrl">Absolute frontend deep link to the single-review view.</param>
public sealed record ExpertCodeReviewRunResponse(
    string JobId,
    string ReviewXml,
    IReadOnlyList<string> ReviewedFiles,
    IReadOnlyList<string> OutOfScopeFiles,
    string ReviewId,
    DateTimeOffset ReviewCreatedAtUtc,
    string ReviewGroupId,
    string ReviewViewUrl
);
