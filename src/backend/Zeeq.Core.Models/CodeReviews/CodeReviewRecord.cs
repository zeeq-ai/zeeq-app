namespace Zeeq.Core.Models;

/// <summary>
/// Durable source of truth for one code review execution.
/// </summary>
/// <remarks>
/// NOTE: This entity maps to the partitioned <c>code_review_records</c> table. The
/// table is range-partitioned by <see cref="DomainEntityBase.CreatedAtUtc" />
/// and uses <c>(id, created_at_utc)</c> as its primary key because PostgreSQL requires
/// partition keys to participate in unique constraints. The pg_partman extension manages
/// child partition creation and retention metadata for this table. Future migrations that
/// add columns, constraints, indexes, or foreign keys must account for the partitioned
/// parent and existing child partitions; do not assume a normal EF table migration is
/// sufficient. Any table that points at review rows must carry both the row ID and created
/// timestamp when it needs an exact partition-aware reference.
/// </remarks>
public sealed class CodeReviewRecord : MutableDomainEntityBase, IOrganizationScopedEntity
{
    /// <summary>
    /// Valid JSONB payload used when no source telemetry was surfaced for a review.
    /// </summary>
    public const string EmptySourceTelemetryPayload = "{}";

    /// <inheritdoc />
    public required string OrganizationId { get; init; }

    /// <summary>
    /// Optional Zeeq team context for this review.
    /// </summary>
    public string? TeamId { get; init; }

    /// <summary>
    /// Pull request record being reviewed. Null for <see cref="CodeReviewRequestOrigin.Agent"/>
    /// reviews created from an MCP uploaded diff — those have no pull request. Always set for
    /// webhook/manual (PR) reviews.
    /// </summary>
    public string? PullRequestRecordId { get; init; }

    /// <summary>
    /// Local repository mapping id. Null for agent reviews whose owner/repo could not be
    /// resolved to a configured repository (the runner falls back to the default reviewer).
    /// Always set for PR reviews.
    /// </summary>
    public string? RepositoryId { get; init; }

    /// <summary>
    /// Stable coding-agent session id for MCP-originated reviews. Null for PR reviews.
    /// Survives across resumed sessions where <see cref="ReviewGroupId"/> may be lost,
    /// so it anchors previous-review context for agent runs.
    /// </summary>
    public string? AgentSessionId { get; set; }

    /// <summary>
    /// Provider-qualified repository name.
    /// </summary>
    public required string OwnerQualifiedRepoName { get; set; }

    /// <summary>
    /// Provider pull request number.
    /// </summary>
    public int PullRequestNumber { get; init; }

    /// <summary>
    /// Branch reviewed.
    /// </summary>
    public required string Branch { get; set; }

    /// <summary>
    /// Pull request title when the review was created.
    /// </summary>
    public required string Title { get; set; }

    /// <summary>
    /// Provider login of the pull request author.
    /// </summary>
    public required string AuthorLogin { get; set; }

    /// <summary>
    /// Current execution status.
    /// </summary>
    public CodeReviewStatus Status { get; set; }

    /// <summary>
    /// Source that requested this review.
    /// </summary>
    public CodeReviewRequestOrigin RequestOrigin { get; set; }

    /// <summary>
    /// Optional group id tying related review attempts together.
    /// </summary>
    public string? ReviewGroupId { get; set; }

    /// <summary>
    /// Stable id of the completed review that this attempt followed in the same review group.
    /// </summary>
    /// <remarks>
    /// This is a causal diagnostic relationship, not a foreign key: review rows are partitioned
    /// by creation time, and concurrent follow-ups may legitimately point to the same predecessor.
    /// </remarks>
    public string? PreviousReviewId { get; set; }

    /// <summary>
    /// W3C <c>traceparent</c> for this review's root OpenTelemetry activity.
    /// </summary>
    public string? ExecutionTraceParent { get; set; }

    /// <summary>
    /// Optional W3C <c>tracestate</c> paired with <see cref="ExecutionTraceParent"/>.
    /// </summary>
    public string? ExecutionTraceState { get; set; }

    /// <summary>
    /// Remaining organization review budget after this review is accepted.
    /// </summary>
    public int RemainingReviewBudget { get; set; }

    /// <summary>
    /// Number of critical findings produced by this review.
    /// </summary>
    public int CriticalFindings { get; set; }

    /// <summary>
    /// Number of major findings produced by this review.
    /// </summary>
    public int MajorFindings { get; set; }

    /// <summary>
    /// Number of minor findings produced by this review.
    /// </summary>
    public int MinorFindings { get; set; }

    /// <summary>
    /// Number of suggestion-level findings produced by this review.
    /// </summary>
    public int SuggestionFindings { get; set; }

    /// <summary>
    /// Number of informational comment findings produced by this review.
    /// </summary>
    public int CommentFindings { get; set; }

    /// <summary>
    /// Compact per-review jsonb payload holding the serialized <c>CodeReviewSourceTelemetry</c>
    /// (which KB documents/snippets the reviewers consulted, tool usage, missed queries);
    /// <see cref="EmptySourceTelemetryPayload"/> when no sources were surfaced.
    /// </summary>
    /// <remarks>
    /// Small aggregated metadata kept on the row (not a blob) so it loads with the record on every
    /// surface and is present even for zero-finding reviews. Renamed from the never-populated
    /// <c>FindingsPayload</c> (findings live in the external XML artifact,
    /// <see cref="FindingsStorageUri"/>). Read/written via
    /// <c>CodeReviewSourceTelemetrySerializer</c>.
    ///
    /// NOTE: kept as a <c>string</c> (jsonb) + a manual serializer rather than an EF 10
    /// <c>ToJson()</c> / Npgsql POCO-as-jsonb mapping. The telemetry record lives in
    /// <c>Zeeq.Platform.CodeReviews</c> (a higher layer than this domain model), so a typed JSON
    /// navigation would invert layering; a bad or schema-drifted payload would also throw during
    /// entity materialization and break every hot read path, whereas the manual deserialize
    /// degrades to "no panel". In-DB querying (the only real <c>ToJson()</c> upside) is out of
    /// scope — analytics runs as an offline ETL over this payload.
    /// </remarks>
    public string SourceTelemetryPayload { get; set; } = EmptySourceTelemetryPayload;

    /// <summary>
    /// External storage URI for large findings payloads.
    /// </summary>
    public string? FindingsStorageUri { get; set; }

    /// <summary>
    /// Failure text for errored reviews.
    /// </summary>
    public string? FailureMessage { get; set; }
}
