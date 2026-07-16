using System.ComponentModel.DataAnnotations;
using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Error response for code-review API validation failures.
/// </summary>
/// <param name="Code">Stable machine-readable error code.</param>
/// <param name="Message">Human-readable description of the validation failure.</param>
public sealed record CodeReviewEndpointError(string Code, string Message);

/// <summary>
/// Response returned after an MCP uploaded-diff body is accepted.
/// </summary>
/// <param name="JobId">Uploaded-diff job id.</param>
/// <param name="ByteCount">Number of raw request-body bytes stored.</param>
/// <param name="ExpiresAtUtc">Token expiry shared by the upload and review run.</param>
public sealed record CodeReviewMcpDiffUploadResponse(
    string JobId,
    int ByteCount,
    DateTimeOffset ExpiresAtUtc
);

/// <summary>
/// DTO cursor for streams ordered newest first by <c>CreatedAtUtc, Id</c>.
/// </summary>
/// <param name="CreatedAtUtc">Partition timestamp for the last row in the page.</param>
/// <param name="Id">Stable row id used as the cursor tie-breaker.</param>
public sealed record CodeReviewStreamCursorDto(DateTimeOffset CreatedAtUtc, string Id);

/// <summary>
/// DTO cursor for inbox review update polling.
/// </summary>
/// <param name="ReviewCreatedAtLowerBoundUtc">Review creation timestamp lower bound used for partition pruning.</param>
/// <param name="UpdatedAtUtc">Update timestamp high-water mark for the polling stream.</param>
/// <param name="CreatedAtUtc">Review creation timestamp tie-breaker for updates with the same update time.</param>
/// <param name="Id">Review row id tie-breaker for updates with the same update and creation timestamps.</param>
/// <param name="TeamId">Effective team filter that produced this cursor, when any.</param>
/// <param name="RepositoryId">Effective repository filter that produced this cursor, when any.</param>
/// <param name="Scope">Effective inbox scope that produced this cursor.</param>
/// <param name="SubjectUserId">Server-derived user id for mine-scoped cursors, when applicable.</param>
public sealed record CodeReviewUpdateCursorDto(
    DateTimeOffset ReviewCreatedAtLowerBoundUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset CreatedAtUtc,
    string Id,
    string? TeamId,
    string? RepositoryId,
    CodeReviewInboxScope Scope,
    string? SubjectUserId
);

/// <summary>
/// Pull request row returned to the code-review inbox.
/// </summary>
/// <param name="Id">Stable pull request record id.</param>
/// <param name="CreatedAtUtc">Pull request record creation timestamp and partition key.</param>
/// <param name="UpdatedAtUtc">Most recent time this pull request row was updated.</param>
/// <param name="OrganizationId">Organization that owns the pull request row.</param>
/// <param name="TeamId">Team associated with the pull request row, when any.</param>
/// <param name="RepositoryId">Configured Zeeq repository id for this pull request.</param>
/// <param name="OwnerQualifiedRepoName">Provider-qualified repository name, such as owner/repo.</param>
/// <param name="PullRequestNumber">Provider pull request number.</param>
/// <param name="Title">Current pull request title.</param>
/// <param name="Branch">Current source branch name.</param>
/// <param name="BaseBranch">Current target branch name.</param>
/// <param name="HeadSha">Current pull request head commit SHA.</param>
/// <param name="AuthorLogin">Provider login for the pull request author.</param>
/// <param name="HtmlUrl">Browser URL for the pull request.</param>
/// <param name="IsDraft">True when the pull request is currently a draft.</param>
/// <param name="State">Current provider pull request lifecycle state.</param>
/// <param name="ClaimStatus">Current Zeeq claim state for review assignment.</param>
/// <param name="ClaimedByUserId">Zeeq user id that claimed the pull request, when claimed.</param>
/// <param name="FeatureId">Optional Zeeq feature id associated with the pull request.</param>
/// <param name="LastWebhookAtUtc">Most recent webhook time observed for this pull request.</param>
/// <param name="SingleViewToken">Compact URL-safe token encoding (CreatedAtUtc, mode=Pr) for the Mode 1 share link.</param>
/// <param name="CheckRunBlocking">True when the PR has an active blocking check run.</param>
public sealed record CodeReviewPullRequestDto(
    string Id,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string OrganizationId,
    string? TeamId,
    string RepositoryId,
    string OwnerQualifiedRepoName,
    int PullRequestNumber,
    string Title,
    string Branch,
    string BaseBranch,
    string HeadSha,
    string AuthorLogin,
    string HtmlUrl,
    bool IsDraft,
    PullRequestState State,
    PullRequestClaimStatus ClaimStatus,
    string? ClaimedByUserId,
    string? FeatureId,
    DateTimeOffset LastWebhookAtUtc,
    string SingleViewToken,
    bool CheckRunBlocking
);

/// <summary>
/// Review row returned for one selected pull request.
/// </summary>
/// <param name="Id">Stable code review record id.</param>
/// <param name="CreatedAtUtc">Review creation timestamp and partition key.</param>
/// <param name="UpdatedAtUtc">Most recent time this review row was updated.</param>
/// <param name="PullRequestRecordId">Stable pull request record id reviewed by this execution. Null for agent (MCP) reviews.</param>
/// <param name="RepositoryId">Configured Zeeq repository id for this review. Null for agent reviews without a resolved repository.</param>
/// <param name="OwnerQualifiedRepoName">Provider-qualified repository name, such as owner/repo.</param>
/// <param name="PullRequestNumber">Provider pull request number reviewed by this execution.</param>
/// <param name="Branch">Source branch reviewed by this execution.</param>
/// <param name="Title">Pull request title captured when the review was created.</param>
/// <param name="AuthorLogin">Provider login for the pull request author.</param>
/// <param name="Status">Current review execution status.</param>
/// <param name="RequestOrigin">Source that requested this review execution.</param>
/// <param name="ReviewGroupId">Optional group id tying related review attempts together.</param>
/// <param name="RemainingReviewBudget">Remaining review budget after this review was accepted.</param>
/// <param name="CriticalFindings">Number of critical findings produced by the review.</param>
/// <param name="MajorFindings">Number of major findings produced by the review.</param>
/// <param name="MinorFindings">Number of minor findings produced by the review.</param>
/// <param name="SuggestionFindings">Number of suggestion-level findings produced by the review.</param>
/// <param name="CommentFindings">Number of informational comment findings produced by the review.</param>
/// <param name="FindingsStorageUri">Storage URI for the full review findings artifact, when available.</param>
/// <param name="FailureMessage">Failure text for errored reviews, when available.</param>
/// <param name="HasSourceTelemetry">Hint that a non-empty source-telemetry payload is stored and fetchable via the findings endpoint; the findings endpoint remains the source of truth for the actual content.</param>
public sealed record CodeReviewRecordDto(
    string Id,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? PullRequestRecordId,
    string? RepositoryId,
    string OwnerQualifiedRepoName,
    int PullRequestNumber,
    string Branch,
    string Title,
    string AuthorLogin,
    CodeReviewStatus Status,
    CodeReviewRequestOrigin RequestOrigin,
    string? ReviewGroupId,
    int RemainingReviewBudget,
    int CriticalFindings,
    int MajorFindings,
    int MinorFindings,
    int SuggestionFindings,
    int CommentFindings,
    string? FindingsStorageUri,
    string? FailureMessage,
    bool HasSourceTelemetry
);

/// <summary>
/// Detailed findings for one completed code-review record.
/// </summary>
/// <param name="CodeReviewRecordId">Stable code review record id that owns these findings.</param>
/// <param name="CodeReviewCreatedAtUtc">Review creation timestamp and partition key.</param>
/// <param name="NoAgentsActivated">True when configured reviewer agents existed but none matched the reviewed files.</param>
/// <param name="Reviews">Reviewer facet blocks parsed from the findings artifact.</param>
/// <param name="SourceTelemetry">Which KB documents/snippets the reviewers consulted; null when none.</param>
public sealed record CodeReviewFindingsResponse(
    string CodeReviewRecordId,
    DateTimeOffset CodeReviewCreatedAtUtc,
    bool NoAgentsActivated,
    IReadOnlyList<CodeReviewReviewerFindingsDto> Reviews,
    CodeReviewSourceTelemetryDto? SourceTelemetry
);

/// <summary>
/// Findings and summary text emitted by one reviewer facet.
/// </summary>
/// <param name="Facet">Reviewer facet label, such as Security or Performance.</param>
/// <param name="Agent">Reviewer display name.</param>
/// <param name="Summary">Short reviewer summary.</param>
/// <param name="Details">Expanded reviewer details.</param>
/// <param name="Findings">Actionable findings emitted by this reviewer.</param>
public sealed record CodeReviewReviewerFindingsDto(
    string Facet,
    string Agent,
    string Summary,
    string Details,
    IReadOnlyList<CodeReviewFindingDto> Findings
);

/// <summary>
/// One actionable finding parsed from a review artifact.
/// </summary>
/// <param name="Level">Finding severity level.</param>
/// <param name="File">Repository-relative file path.</param>
/// <param name="Line">Optional file line for inline findings.</param>
/// <param name="Side">Optional GitHub diff side, such as RIGHT or LEFT.</param>
/// <param name="Summary">Short finding summary.</param>
/// <param name="Body">Full finding explanation.</param>
public sealed record CodeReviewFindingDto(
    CodeReviewFindingLevel Level,
    string File,
    int? Line,
    string? Side,
    string Summary,
    string Body
);

/// <summary>
/// Readable API projection of the review's source telemetry (which KB documents/snippets the
/// reviewers consulted, tool usage, and content-gap misses).
/// </summary>
/// <remarks>
/// This is a decoupled, self-describing shape (full property names, no compact wire keys) mapped
/// server-side from the compact storage record <c>CodeReviewSourceTelemetry</c>. Keeping the API
/// DTO separate means a storage <c>schemaVersion</c> bump does not break the generated TypeScript
/// client or OpenAPI contract. Ordering and caps are already applied by the stored snapshot.
/// </remarks>
/// <param name="SchemaVersion">Storage schema version the payload was produced with.</param>
/// <param name="Summary">Roll-up counts across the snapshot.</param>
/// <param name="Documents">Consulted documents, ordered by importance.</param>
/// <param name="ToolUsage">Per-tool call/success/failure counts.</param>
/// <param name="MissedQueries">Searches that returned nothing (content gaps).</param>
public sealed record CodeReviewSourceTelemetryDto(
    int SchemaVersion,
    CodeReviewSourceSummaryDto Summary,
    IReadOnlyList<CodeReviewSourceDocumentDto> Documents,
    IReadOnlyList<CodeReviewToolUsageDto> ToolUsage,
    IReadOnlyList<CodeReviewMissedQueryDto> MissedQueries
);

/// <summary>Roll-up counts for a <see cref="CodeReviewSourceTelemetryDto" />.</summary>
public sealed record CodeReviewSourceSummaryDto(
    int DocumentCount,
    int SnippetCount,
    int SourceHitCount,
    int ToolCallCount,
    int MissedQueryCount
);

/// <summary>One consulted document and the snippets surfaced within it.</summary>
/// <param name="DocumentId">Stable document id (for cross-revision joins).</param>
/// <param name="Library">Library the document belongs to.</param>
/// <param name="Path">Document path at review time.</param>
/// <param name="Title">Document title at review time.</param>
/// <param name="HitCount">Document-level plus all snippet hits.</param>
/// <param name="Usages">Distinct usages observed (Searched, Read).</param>
/// <param name="ReadAfterSearch">Whether the doc was searched and later read (relevance proxy).</param>
/// <param name="Facets">Distinct reviewer facets that surfaced this document.</param>
/// <param name="BestRank">Min 1-based rank across search arms; 0 when never ranked.</param>
/// <param name="BestScore">Max fused relevance score across hits.</param>
/// <param name="Queries">Distinct whole-document queries.</param>
/// <param name="Snippets">Snippets surfaced within this document, ordered by importance.</param>
public sealed record CodeReviewSourceDocumentDto(
    string DocumentId,
    string Library,
    string Path,
    string Title,
    int HitCount,
    IReadOnlyList<string> Usages,
    bool ReadAfterSearch,
    IReadOnlyList<string> Facets,
    int BestRank,
    double BestScore,
    IReadOnlyList<string> Queries,
    IReadOnlyList<CodeReviewSourceSnippetDto> Snippets
);

/// <summary>One snippet (prose section or code sample) surfaced within a document.</summary>
/// <param name="SnippetId">Stable snippet id (for cross-revision joins).</param>
/// <param name="Heading">Snippet heading path.</param>
/// <param name="Kind">Snippet kind: Section or CodeSample.</param>
/// <param name="Language">Fence language for code samples; null for sections.</param>
/// <param name="HitCount">Number of hits that surfaced this snippet.</param>
/// <param name="Facets">Distinct reviewer facets that surfaced this snippet.</param>
/// <param name="BestRank">Min 1-based rank across search arms; 0 when never ranked.</param>
/// <param name="BestScore">Max fused relevance score across hits.</param>
/// <param name="IdentifierMatch">Whether any contributing hit had an identifier overlap.</param>
/// <param name="Queries">Distinct queries that surfaced this snippet.</param>
public sealed record CodeReviewSourceSnippetDto(
    string SnippetId,
    string Heading,
    string Kind,
    string? Language,
    int HitCount,
    IReadOnlyList<string> Facets,
    int BestRank,
    double BestScore,
    bool IdentifierMatch,
    IReadOnlyList<string> Queries
);

/// <summary>Per-tool invocation counts observed during a review run.</summary>
public sealed record CodeReviewToolUsageDto(string Tool, int Calls, int Succeeded, int Failed);

/// <summary>A search that returned zero rows — the content-gap signal.</summary>
public sealed record CodeReviewMissedQueryDto(
    string Query,
    string Tool,
    IReadOnlyList<string> Facets
);

/// <summary>
/// Minimal review update row for patching loaded PR inbox state.
/// </summary>
/// <param name="PullRequestRecordId">Stable pull request record id affected by the update.</param>
/// <param name="PullRequestCreatedAtUtc">Pull request record creation timestamp and partition key.</param>
/// <param name="CodeReviewRecordId">Stable code review record id that changed.</param>
/// <param name="CodeReviewCreatedAtUtc">Review creation timestamp and partition key.</param>
/// <param name="Status">Current review execution status.</param>
/// <param name="CriticalFindings">Number of critical findings on the review.</param>
/// <param name="MajorFindings">Number of major findings on the review.</param>
/// <param name="MinorFindings">Number of minor findings on the review.</param>
/// <param name="SuggestionFindings">Number of suggestion-level findings on the review.</param>
/// <param name="CommentFindings">Number of informational comment findings on the review.</param>
/// <param name="RemainingReviewBudget">Remaining review budget after this review was accepted.</param>
/// <param name="UpdatedAtUtc">Most recent time this review row was updated.</param>
public sealed record CodeReviewInboxUpdateDto(
    string PullRequestRecordId,
    DateTimeOffset PullRequestCreatedAtUtc,
    string CodeReviewRecordId,
    DateTimeOffset CodeReviewCreatedAtUtc,
    CodeReviewStatus Status,
    int CriticalFindings,
    int MajorFindings,
    int MinorFindings,
    int SuggestionFindings,
    int CommentFindings,
    int RemainingReviewBudget,
    DateTimeOffset UpdatedAtUtc
);

/// <summary>
/// Response for the PR inbox list.
/// </summary>
/// <param name="Items">Pull request rows in newest-first order.</param>
/// <param name="NextCursor">Cursor for loading the next older page, when available.</param>
/// <param name="NewestCursor">Cursor for the newest row in this response, when available.</param>
/// <param name="ReviewUpdatesCursor">Initial cursor for polling minimal review updates for this inbox window.</param>
public sealed record CodeReviewPullRequestListResponse(
    IReadOnlyList<CodeReviewPullRequestDto> Items,
    CodeReviewStreamCursorDto? NextCursor,
    CodeReviewStreamCursorDto? NewestCursor,
    CodeReviewUpdateCursorDto? ReviewUpdatesCursor
);

/// <summary>
/// Response for one selected pull request.
/// </summary>
/// <param name="PullRequest">Selected pull request row.</param>
public sealed record CodeReviewPullRequestDetailResponse(CodeReviewPullRequestDto PullRequest);

/// <summary>
/// Response for one selected pull request's review history.
/// </summary>
/// <param name="Items">Review rows for the selected pull request in newest-first order.</param>
/// <param name="NextCursor">Cursor for loading the next older page, when available.</param>
/// <param name="NewestCursor">Cursor for the newest row in this response, when available.</param>
public sealed record CodeReviewPullRequestReviewListResponse(
    IReadOnlyList<CodeReviewRecordDto> Items,
    CodeReviewStreamCursorDto? NextCursor,
    CodeReviewStreamCursorDto? NewestCursor
);

/// <summary>
/// Response for manually requesting a new review for a selected pull request.
/// </summary>
/// <param name="Outcome">Durable outcome of the request attempt.</param>
/// <param name="CommentKind">Immediate GitHub comment render kind that was published.</param>
/// <param name="PullRequest">Pull request row used for the request.</param>
/// <param name="CodeReview">Created or referenced code review row, when the outcome has one.</param>
public sealed record CodeReviewManualRequestResponse(
    CodeReviewRequestOutcome Outcome,
    string CommentKind,
    CodeReviewPullRequestDto PullRequest,
    CodeReviewRecordDto? CodeReview
);

/// <summary>
/// Organization-level code-review execution settings.
/// </summary>
/// <param name="OrganizationId">Organization these settings apply to.</param>
/// <param name="MaxConcurrentReviews">Maximum number of code reviews that may run concurrently for the organization.</param>
/// <param name="ExecutionLeaseDurationMinutes">Execution lease duration in whole minutes.</param>
public sealed record CodeReviewOrganizationSettingsResponse(
    string OrganizationId,
    int MaxConcurrentReviews,
    int ExecutionLeaseDurationMinutes
);

/// <summary>
/// Request for saving organization-level code-review execution limits.
/// </summary>
/// <param name="MaxConcurrentReviews">Maximum number of code reviews that may run concurrently for the organization.</param>
public sealed record SaveCodeReviewOrganizationSettingsRequest(
    [property: Range(
        SaveCodeReviewOrganizationSettingsHandler.MinimumMaxConcurrentReviews,
        SaveCodeReviewOrganizationSettingsHandler.MaximumMaxConcurrentReviews
    )]
        int MaxConcurrentReviews
);

/// <summary>
/// Persisted reviewer-agent configuration returned to management screens.
/// </summary>
/// <param name="Id">Stable reviewer-agent id.</param>
/// <param name="CreatedAtUtc">Timestamp when the agent configuration was created.</param>
/// <param name="UpdatedAtUtc">Most recent timestamp when the agent configuration changed.</param>
/// <param name="RepositoryId">Repository mapping this agent belongs to.</param>
/// <param name="TeamId">Team context inherited from the repository mapping, when any.</param>
/// <param name="DisplayName">Human-readable reviewer name shown in management UI and comments.</param>
/// <param name="ReviewFacet">Review facet label produced by this agent.</param>
/// <param name="ModelTier">Semantic Zeeq model tier resolved through organization LLM settings at runtime.</param>
/// <param name="Prompt">Reviewer-specific instructions appended to the shared review prompt.</param>
/// <param name="Enabled">Whether the persisted agent can participate in future reviews.</param>
/// <param name="ActivationConfiguration">File activation filters that decide whether this agent runs for a PR.</param>
public sealed record CodeReviewerAgentDto(
    string Id,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string RepositoryId,
    string? TeamId,
    string DisplayName,
    string ReviewFacet,
    CodeReviewModelTier ModelTier,
    string Prompt,
    bool Enabled,
    CodeReviewerActivationConfigurationDto ActivationConfiguration
);

/// <summary>
/// A code-defined, clonable reviewer-agent template surfaced in the management UI.
/// </summary>
/// <remarks>
/// Templates are not persisted and carry no organization, repository, or
/// timestamp identity. They exist only to seed a new agent's form, so the shape
/// is intentionally narrower than <see cref="CodeReviewerAgentDto" />.
/// </remarks>
/// <param name="Key">Stable template identifier used for selection and telemetry.</param>
/// <param name="DisplayName">Human-readable persona name shown in the library.</param>
/// <param name="ReviewFacet">Facet label this persona owns.</param>
/// <param name="Description">Short summary describing when to reach for this persona.</param>
/// <param name="ModelTier">Semantic Zeeq model tier the persona defaults to.</param>
/// <param name="Prompt">Reviewer instructions seeded into a new agent's prompt.</param>
/// <param name="ActivationConfiguration">Default file activation rules for the persona.</param>
public sealed record CodeReviewerAgentTemplateDto(
    string Key,
    string DisplayName,
    string ReviewFacet,
    string Description,
    CodeReviewModelTier ModelTier,
    string Prompt,
    CodeReviewerActivationConfigurationDto ActivationConfiguration
);

/// <summary>
/// One repository-relative file matching rule for repository review filters.
/// </summary>
/// <param name="MatchType">How the pattern should be interpreted.</param>
/// <param name="Pattern">Repository-relative path, prefix, extension, or glob pattern.</param>
public sealed record CodeReviewFileMatchCriteriaDto(
    CodeReviewFileNameMatchType MatchType,
    [property: Required, MaxLength(1024)] string Pattern
);

/// <summary>
/// Include and exclude rules applied before reviewer-agent activation.
/// </summary>
/// <param name="IncludedFiles">Allowlist rules. Empty means all candidate files are allowed.</param>
/// <param name="ExcludedFiles">Deny rules that always win over includes.</param>
public sealed record CodeReviewFileFilterDto(
    IReadOnlyList<CodeReviewFileMatchCriteriaDto> IncludedFiles,
    IReadOnlyList<CodeReviewFileMatchCriteriaDto> ExcludedFiles
);

/// <summary>
/// File activation rules for one persisted reviewer agent.
/// </summary>
/// <param name="IncludedFiles">Allowlist rules. Empty means all repository-scoped files can activate the agent.</param>
/// <param name="ExcludedFiles">Deny rules that always win over includes.</param>
public sealed record CodeReviewerActivationConfigurationDto(
    IReadOnlyList<CodeReviewFileMatchCriteriaDto> IncludedFiles,
    IReadOnlyList<CodeReviewFileMatchCriteriaDto> ExcludedFiles
);

/// <summary>
/// Check-run gating settings for a repository.
/// </summary>
/// <param name="BlockOnCritical">Block when the review has at least one Critical finding.</param>
/// <param name="BlockOnMajor">Block when the review has at least one Major finding. Implies Critical.</param>
public sealed record CodeReviewCheckRunConfigurationDto(bool BlockOnCritical, bool BlockOnMajor);

/// <summary>
/// Repository-level review configuration stored in typed JSONB.
/// </summary>
/// <param name="FileFilter">Repository-level file filter applied before agent activation.</param>
public sealed record CodeReviewRepositoryConfigurationDto(CodeReviewFileFilterDto FileFilter)
{
    /// <summary>
    /// Check-run gating settings for this repository.
    /// Omitted or null means the feature is disabled (backward compatible).
    /// </summary>
    public CodeReviewCheckRunConfigurationDto? CheckRun { get; init; }

    /// <summary>
    /// Shared prompt fragment injected into every reviewer agent's prompt for
    /// this repository. Omitted or null means no organization-wide guidance
    /// is added (backward compatible).
    /// </summary>
    public string? SharedPromptFragment { get; init; }
}

/// <summary>
/// Response for reading repository-level review configuration.
/// </summary>
/// <param name="RepositoryId">Configured Zeeq repository id.</param>
/// <param name="Configuration">Current repository review configuration.</param>
public sealed record CodeReviewRepositoryConfigurationResponse(
    string RepositoryId,
    CodeReviewRepositoryConfigurationDto Configuration
);

/// <summary>
/// Request for saving repository-level review configuration.
/// </summary>
/// <param name="Configuration">Replacement repository review configuration.</param>
public sealed record SaveCodeReviewRepositoryConfigurationRequest(
    [property: Required] CodeReviewRepositoryConfigurationDto Configuration
);

/// <summary>
/// Response for repository reviewer-agent listing.
/// </summary>
/// <param name="Items">Persisted reviewer agents for the repository.</param>
public sealed record CodeReviewerAgentListResponse(IReadOnlyList<CodeReviewerAgentDto> Items);

/// <summary>
/// Response for the built-in reviewer-agent template catalog.
/// </summary>
/// <param name="Items">Clonable reviewer-agent templates, in display order.</param>
public sealed record CodeReviewerAgentTemplateListResponse(
    IReadOnlyList<CodeReviewerAgentTemplateDto> Items
);

/// <summary>
/// Response for a single persisted reviewer-agent mutation.
/// </summary>
/// <param name="Agent">Persisted reviewer-agent configuration after the mutation.</param>
public sealed record CodeReviewerAgentResponse(CodeReviewerAgentDto Agent);

/// <summary>
/// Request for creating a repository reviewer agent.
/// </summary>
/// <param name="DisplayName">Human-readable reviewer name shown in management UI and comments.</param>
/// <param name="ReviewFacet">Review facet label produced by this agent.</param>
/// <param name="ModelTier">Semantic Zeeq model tier resolved through organization LLM settings at runtime.</param>
/// <param name="Prompt">Reviewer-specific instructions appended to the shared review prompt.</param>
/// <param name="Enabled">Whether the persisted agent can participate in future reviews.</param>
/// <param name="ActivationConfiguration">File activation filters that decide whether this agent runs for a PR.</param>
public sealed record CreateCodeReviewerAgentRequest(
    [property: Required, MaxLength(256)] string DisplayName,
    [property: Required, MaxLength(128)] string ReviewFacet,
    CodeReviewModelTier ModelTier,
    [property: Required, MaxLength(20_000)] string Prompt,
    bool Enabled,
    [property: Required] CodeReviewerActivationConfigurationDto ActivationConfiguration
);

/// <summary>
/// Request for replacing editable repository reviewer-agent fields.
/// </summary>
/// <param name="DisplayName">Human-readable reviewer name shown in management UI and comments.</param>
/// <param name="ReviewFacet">Review facet label produced by this agent.</param>
/// <param name="ModelTier">Semantic Zeeq model tier resolved through organization LLM settings at runtime.</param>
/// <param name="Prompt">Reviewer-specific instructions appended to the shared review prompt.</param>
/// <param name="Enabled">Whether the persisted agent can participate in future reviews.</param>
/// <param name="ActivationConfiguration">File activation filters that decide whether this agent runs for a PR.</param>
public sealed record UpdateCodeReviewerAgentRequest(
    [property: Required, MaxLength(256)] string DisplayName,
    [property: Required, MaxLength(128)] string ReviewFacet,
    CodeReviewModelTier ModelTier,
    [property: Required, MaxLength(20_000)] string Prompt,
    bool Enabled,
    [property: Required] CodeReviewerActivationConfigurationDto ActivationConfiguration
);

/// <summary>
/// Response for the inbox review update feed.
/// </summary>
/// <param name="Items">Minimal review updates in ascending update order.</param>
/// <param name="NextCursor">Cursor for polling after the last update in this response.</param>
/// <param name="NewestCursor">Cursor for the newest update in this response.</param>
public sealed record CodeReviewInboxUpdateListResponse(
    IReadOnlyList<CodeReviewInboxUpdateDto> Items,
    CodeReviewUpdateCursorDto? NextCursor,
    CodeReviewUpdateCursorDto? NewestCursor
);

internal static class CodeReviewEndpointMapping
{
    public static CodeReviewPullRequestDto ToDto(PullRequestRecord record) =>
        new(
            record.Id,
            record.CreatedAtUtc,
            record.UpdatedAtUtc,
            record.OrganizationId,
            record.TeamId,
            record.RepositoryId,
            record.OwnerQualifiedRepoName,
            record.PullRequestNumber,
            record.Title,
            record.Branch,
            record.BaseBranch,
            record.HeadSha,
            record.AuthorLogin,
            record.HtmlUrl,
            record.IsDraft,
            record.State,
            record.ClaimStatus,
            record.ClaimedByUserId,
            record.FeatureId,
            record.LastWebhookAtUtc,
            CodeReviewSingleViewToken.Encode(record.CreatedAtUtc, CodeReviewSingleViewMode.Pr),
            record.CheckRunState?.State == CheckRunBlockState.Blocking
        );

    public static CodeReviewRecordDto ToDto(CodeReviewRecord record) =>
        new(
            record.Id,
            record.CreatedAtUtc,
            record.UpdatedAtUtc,
            record.PullRequestRecordId,
            record.RepositoryId,
            record.OwnerQualifiedRepoName,
            record.PullRequestNumber,
            record.Branch,
            record.Title,
            record.AuthorLogin,
            record.Status,
            record.RequestOrigin,
            record.ReviewGroupId,
            record.RemainingReviewBudget,
            record.CriticalFindings,
            record.MajorFindings,
            record.MinorFindings,
            record.SuggestionFindings,
            record.CommentFindings,
            record.FindingsStorageUri,
            record.FailureMessage,
            HasSourceTelemetry(record)
        );

    public static CodeReviewInboxUpdateDto ToDto(CodeReviewInboxUpdate update) =>
        new(
            update.PullRequestRecordId,
            update.PullRequestCreatedAtUtc,
            update.CodeReviewRecordId,
            update.CodeReviewCreatedAtUtc,
            update.Status,
            update.CriticalFindings,
            update.MajorFindings,
            update.MinorFindings,
            update.SuggestionFindings,
            update.CommentFindings,
            update.RemainingReviewBudget,
            update.UpdatedAtUtc
        );

    public static CodeReviewFindingsResponse ToFindingsDto(
        CodeReviewRecord record,
        CodeReviewOutputDocument findings,
        CodeReviewSourceTelemetry? sourceTelemetry
    ) =>
        new(
            record.Id,
            record.CreatedAtUtc,
            findings.NoAgentsActivated,
            findings.Reviews.Select(ToFindingsDto).ToArray(),
            ToDto(sourceTelemetry)
        );

    public static CodeReviewFindingsResponse ToEmptyFindingsDto(
        CodeReviewRecord record,
        CodeReviewSourceTelemetry? sourceTelemetry
    ) =>
        new(
            record.Id,
            record.CreatedAtUtc,
            NoAgentsActivated: false,
            Reviews: [],
            SourceTelemetry: ToDto(sourceTelemetry)
        );

    /// <summary>
    /// Projects the compact storage telemetry record to the readable API DTO. This is the single
    /// storage→API translation point; compact wire keys never cross the API boundary.
    /// </summary>
    private static CodeReviewSourceTelemetryDto? ToDto(CodeReviewSourceTelemetry? telemetry) =>
        telemetry is null
            ? null
            : new(
                telemetry.SchemaVersion,
                new(
                    telemetry.Summary.DocumentCount,
                    telemetry.Summary.SnippetCount,
                    telemetry.Summary.SourceHitCount,
                    telemetry.Summary.ToolCallCount,
                    telemetry.Summary.MissedQueryCount
                ),
                [.. telemetry.Documents.Select(ToDto)],
                [
                    .. telemetry.ToolUsage.Select(usage => new CodeReviewToolUsageDto(
                        usage.Tool,
                        usage.Calls,
                        usage.Succeeded,
                        usage.Failed
                    )),
                ],
                [
                    .. telemetry.MissedQueries.Select(miss => new CodeReviewMissedQueryDto(
                        miss.Query,
                        miss.Tool,
                        miss.Facets
                    )),
                ]
            );

    private static CodeReviewSourceDocumentDto ToDto(CodeReviewSourceDocument document) =>
        new(
            document.DocumentId,
            document.Library,
            document.Path,
            document.Title,
            document.HitCount,
            document.Usages,
            document.ReadAfterSearch,
            document.Facets,
            document.BestRank,
            document.BestScore,
            document.Queries,
            [
                .. document.Snippets.Select(snippet => new CodeReviewSourceSnippetDto(
                    snippet.SnippetId,
                    snippet.Heading,
                    snippet.Kind,
                    snippet.Language,
                    snippet.HitCount,
                    snippet.Facets,
                    snippet.BestRank,
                    snippet.BestScore,
                    snippet.IdentifierMatch,
                    snippet.Queries
                )),
            ]
        );

    /// <summary>Whether a review carries a non-empty source-telemetry payload worth fetching.</summary>
    /// <remarks>
    /// A deliberately cheap raw-string check — <c>ToDto(CodeReviewRecord)</c> runs per row in list
    /// responses (e.g. ListPullRequestReviews), so parsing the payload here just to compute a flag
    /// would add a JSON deserialize per row and defeat the point of keeping the payload out of list
    /// responses. This is a fetch hint, not a guarantee: the findings endpoint is the source of
    /// truth and returns <c>null</c> for a malformed payload. Divergence is unreachable from our own
    /// writes (the serializer always emits valid JSON, and <c>"{}"</c> is excluded) and the client
    /// degrades gracefully — a null telemetry response simply renders no panel.
    /// </remarks>
    private static bool HasSourceTelemetry(CodeReviewRecord record) =>
        !string.IsNullOrWhiteSpace(record.SourceTelemetryPayload)
        && record.SourceTelemetryPayload != CodeReviewRecord.EmptySourceTelemetryPayload;

    private static CodeReviewReviewerFindingsDto ToFindingsDto(CodeReviewFacetOutput review) =>
        new(
            review.Facet,
            review.Agent,
            review.Summary,
            review.Details,
            [.. review.Findings.Select(ToFindingsDto)]
        );

    private static CodeReviewFindingDto ToFindingsDto(CodeReviewFindingOutput finding) =>
        new(
            finding.Level,
            finding.File,
            finding.Line > 0 ? finding.Line : null,
            finding.Side,
            finding.Summary,
            finding.Details
        );

    public static CodeReviewOrganizationSettingsResponse ToDto(
        string organizationId,
        CodeReviewOrganizationSettings settings
    ) =>
        new(
            organizationId,
            settings.MaxConcurrentReviews,
            (int)settings.ExecutionLeaseDuration.TotalMinutes
        );

    public static CodeReviewerAgentDto ToDto(CodeReviewerAgent agent) =>
        new(
            agent.Id,
            agent.CreatedAtUtc,
            agent.UpdatedAtUtc,
            agent.RepositoryId,
            agent.TeamId,
            agent.DisplayName,
            agent.ReviewFacet,
            agent.ModelTier,
            agent.Prompt,
            agent.Enabled,
            ToDto(agent.ActivationConfiguration)
        );

    public static CodeReviewerAgentTemplateDto ToDto(CodeReviewerAgentTemplate template) =>
        new(
            template.Key,
            template.DisplayName,
            template.ReviewFacet,
            template.Description,
            template.ModelTier,
            template.Prompt,
            ToDto(template.ActivationConfiguration)
        );

    public static CodeReviewerActivationConfigurationDto ToDto(
        CodeReviewerActivationConfiguration configuration
    ) =>
        new(
            configuration.IncludedFiles.Select(ToDto).ToArray(),
            configuration.ExcludedFiles.Select(ToDto).ToArray()
        );

    public static CodeReviewRepositoryConfigurationDto ToDto(
        CodeRepositoryReviewConfiguration configuration
    ) =>
        new(ToDto(configuration.FileFilter))
        {
            CheckRun = configuration.CheckRun.IsEnabled
                ? new(configuration.CheckRun.BlockOnCritical, configuration.CheckRun.BlockOnMajor)
                : null,
            SharedPromptFragment = configuration.SharedPromptFragment,
        };

    public static CodeReviewFileFilterDto ToDto(CodeReviewFileFilter fileFilter) =>
        new(
            fileFilter.IncludedFiles.Select(ToDto).ToArray(),
            fileFilter.ExcludedFiles.Select(ToDto).ToArray()
        );

    public static CodeReviewFileMatchCriteriaDto ToDto(CodeReviewFileMatchCriteria criteria) =>
        new(criteria.MatchType, criteria.Pattern);

    public static CodeRepositoryReviewConfiguration ToModel(
        CodeReviewRepositoryConfigurationDto? configuration
    ) =>
        new()
        {
            FileFilter = ToModel(configuration?.FileFilter),
            CheckRun = configuration?.CheckRun is { } cr
                ? new()
                {
                    BlockOnCritical = cr.BlockOnCritical || cr.BlockOnMajor,
                    BlockOnMajor = cr.BlockOnMajor,
                }
                : CodeRepositoryReviewCheckRunConfiguration.Empty,
            SharedPromptFragment = configuration?.SharedPromptFragment?.Trim() ?? string.Empty,
        };

    public static CodeReviewFileFilter ToModel(CodeReviewFileFilterDto? fileFilter) =>
        new()
        {
            IncludedFiles = fileFilter?.IncludedFiles?.Select(ToModel).ToList() ?? [],
            ExcludedFiles = fileFilter?.ExcludedFiles?.Select(ToModel).ToList() ?? [],
        };

    public static CodeReviewFileMatchCriteria ToModel(CodeReviewFileMatchCriteriaDto criteria) =>
        new() { MatchType = criteria.MatchType, Pattern = criteria.Pattern.Trim() };

    public static CodeReviewerActivationConfiguration ToModel(
        CodeReviewerActivationConfigurationDto? configuration
    ) =>
        new()
        {
            IncludedFiles = configuration?.IncludedFiles?.Select(ToModel).ToList() ?? [],
            ExcludedFiles = configuration?.ExcludedFiles?.Select(ToModel).ToList() ?? [],
        };

    public static CodeReviewStreamCursorDto? ToDto(CodeReviewStreamCursor? cursor) =>
        cursor is null ? null : new(cursor.CreatedAtUtc, cursor.Id);

    public static CodeReviewUpdateCursorDto? ToDto(CodeReviewUpdateCursor? cursor) =>
        cursor is null
            ? null
            : new(
                cursor.ReviewCreatedAtLowerBoundUtc,
                cursor.UpdatedAtUtc,
                cursor.CreatedAtUtc,
                cursor.Id,
                cursor.TeamId,
                cursor.RepositoryId,
                cursor.Scope,
                cursor.SubjectUserId
            );

    public static CodeReviewStreamCursor? ToStreamCursor(
        DateTimeOffset? createdAtUtc,
        string? id
    ) => createdAtUtc is null || string.IsNullOrWhiteSpace(id) ? null : new(createdAtUtc.Value, id);

    public static CodeReviewUpdateCursor? ToUpdateCursor(
        DateTimeOffset? reviewCreatedAtLowerBoundUtc,
        DateTimeOffset? updatedAtUtc,
        DateTimeOffset? createdAtUtc,
        string? id,
        string? teamId,
        string? repositoryId,
        CodeReviewInboxScope? scope,
        string? subjectUserId
    ) =>
        reviewCreatedAtLowerBoundUtc is null
        || updatedAtUtc is null
        || createdAtUtc is null
        || string.IsNullOrWhiteSpace(id)
            ? null
            : new(
                reviewCreatedAtLowerBoundUtc.Value,
                updatedAtUtc.Value,
                createdAtUtc.Value,
                id,
                NormalizeOptionalFilter(teamId),
                NormalizeOptionalFilter(repositoryId),
                scope ?? CodeReviewInboxScope.All,
                NormalizeOptionalFilter(subjectUserId)
            );

    public static bool Matches(
        this CodeReviewUpdateCursor cursor,
        string? teamId,
        string? repositoryId,
        CodeReviewInboxScope scope,
        string? subjectUserId
    ) =>
        string.Equals(cursor.TeamId, NormalizeOptionalFilter(teamId), StringComparison.Ordinal)
        && string.Equals(
            cursor.RepositoryId,
            NormalizeOptionalFilter(repositoryId),
            StringComparison.Ordinal
        )
        && cursor.Scope == scope
        && string.Equals(
            cursor.SubjectUserId,
            NormalizeOptionalFilter(subjectUserId),
            StringComparison.Ordinal
        );

    public static string? NormalizeOptionalFilter(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>
/// Response for a check-run bypass operation.
/// </summary>
/// <param name="PullRequestNumber">Provider PR number that was targeted.</param>
/// <param name="Cleared">True when the blocking check was cleared.</param>
public sealed record BypassCheckRunResponse(int PullRequestNumber, bool Cleared);

/// <summary>
/// Access mode for the single code-review view.
/// </summary>
public enum CodeReviewSingleViewMode
{
    /// <summary>
    /// The review belongs to a pull request; render the PR's review history as the related set.
    /// </summary>
    Pr = 0,

    /// <summary>
    /// The review was initiated by an MCP coding agent; render the agent session's reviews as the related set.
    /// </summary>
    Agent = 1,
}

/// <summary>
/// Response for the single code-review deep-link view.
/// </summary>
/// <param name="Review">The primary review the link targets (expanded first in the UI).</param>
/// <param name="Reviews">Related set to render, newest-first, primary included.</param>
/// <param name="Mode">Resolved access mode.</param>
/// <param name="PullRequest">The reviewed pull request, when the review is tied to one.</param>
public sealed record CodeReviewSingleViewResponse(
    CodeReviewRecordDto Review,
    IReadOnlyList<CodeReviewRecordDto> Reviews,
    CodeReviewSingleViewMode Mode,
    CodeReviewPullRequestDto? PullRequest
);
