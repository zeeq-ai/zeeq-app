using Pgvector;

namespace Zeeq.Core.Models;

/// <summary>Supported disposition values.</summary>
public enum CodeReviewFindingDisposition
{
    /// <summary>FalsePositive.</summary>
    FalsePositive = 0,

    /// <summary>Deferred.</summary>
    Deferred = 1,
}

/// <summary>Supported approvalstate values.</summary>
public enum CodeReviewFindingApprovalState
{
    /// <summary>Open.</summary>
    Open = 0,

    /// <summary>Proposed.</summary>
    Proposed = 1,

    /// <summary>Confirmed.</summary>
    Confirmed = 2,
}

/// <summary>Supported triagechannel values.</summary>
public enum CodeReviewFindingTriageChannel
{
    /// <summary>Ui.</summary>
    Ui = 0,

    /// <summary>GitHub.</summary>
    GitHub = 1,

    /// <summary>Mcp.</summary>
    Mcp = 2,
}

/// <summary>Durable snapshot and current disposition of a review finding.</summary>
public sealed class CodeReviewFindingTriage : MutableDomainEntityBase, IOrganizationScopedEntity
{
    /// <summary>Copy a snapshot before applying a write.</summary>
    public CodeReviewFindingTriage Clone() => (CodeReviewFindingTriage)MemberwiseClone();

    /// <summary>Gets or sets OrganizationId.</summary>
    public required string OrganizationId { get; init; }

    /// <summary>Gets or sets TeamId.</summary>
    public string? TeamId { get; init; } // historical snapshot only (no FK)

    /// <summary>Gets or sets RepositoryId.</summary>
    public string? RepositoryId { get; set; }

    /// <summary>Gets or sets OwnerQualifiedRepoName.</summary>
    public required string OwnerQualifiedRepoName { get; set; } // stored lowercase invariant

    /// <summary>Gets or sets PullRequestNumber.</summary>
    public int? PullRequestNumber { get; init; } // MCP PR=0 normalized to NULL; CHECK (> 0)

    /// <summary>Gets or sets ReviewGroupId.</summary>
    public string? ReviewGroupId { get; init; }

    // Logical composite ref to pg_partman-partitioned code_review_records (NO inbound FK)
    /// <summary>Gets or sets SourceReviewId.</summary>
    public required string SourceReviewId { get; init; }

    /// <summary>Gets or sets SourceReviewCreatedAtUtc.</summary>
    public required DateTimeOffset SourceReviewCreatedAtUtc { get; init; }

    /// <summary>Gets or sets OccurrenceKey.</summary>
    public required string OccurrenceKey { get; init; } // e.g. "F2"

    /// <summary>Gets or sets SemanticId.</summary>
    public required string SemanticId { get; set; } // e.g. "unsanitized-import-payload"

    // Self-contained typed finding snapshot (survives partition/blob expiry)
    /// <summary>Gets or sets Facet.</summary>
    public required string Facet { get; init; }

    /// <summary>Gets or sets Severity.</summary>
    public CodeReviewFindingLevel Severity { get; init; }

    /// <summary>Gets or sets FilePath.</summary>
    public string? FilePath { get; init; } // empty string normalized to NULL

    /// <summary>Gets or sets LineNumber.</summary>
    public int? LineNumber { get; init; } // <= 0 normalized to NULL

    /// <summary>Gets or sets FindingSummary.</summary>
    public required string FindingSummary { get; init; }

    // LWW state + source event watermark + two-actor provenance
    /// <summary>Gets or sets Disposition.</summary>
    public CodeReviewFindingDisposition? Disposition { get; set; }

    /// <summary>Gets or sets ApprovalState.</summary>
    public CodeReviewFindingApprovalState ApprovalState { get; set; } =
        CodeReviewFindingApprovalState.Open;

    /// <summary>Gets or sets CurrentReason.</summary>
    public string? CurrentReason { get; set; }

    /// <summary>Gets or sets Channel.</summary>
    public CodeReviewFindingTriageChannel Channel { get; set; }

    /// <summary>Gets or sets AgentSessionId.</summary>
    public string? AgentSessionId { get; set; }

    /// <summary>Gets or sets SourceUrl.</summary>
    public string? SourceUrl { get; set; }

    /// <summary>Gets or sets LastSourceEventAtUtc.</summary>
    public DateTimeOffset LastSourceEventAtUtc { get; set; } // GitHub comment created_at or DB clock_timestamp()

    /// <summary>Gets or sets ProposedByUserId.</summary>
    public string? ProposedByUserId { get; set; } // nullable for future erasure

    /// <summary>Gets or sets ProposedAtUtc.</summary>
    public DateTimeOffset? ProposedAtUtc { get; set; }

    /// <summary>Gets or sets ConfirmedByUserId.</summary>
    public string? ConfirmedByUserId { get; set; } // nullable for future erasure

    /// <summary>Gets or sets ConfirmedAtUtc.</summary>
    public DateTimeOffset? ConfirmedAtUtc { get; set; }

    /// <summary>Gets or sets ClearedByUserId.</summary>
    public string? ClearedByUserId { get; set; } // nullable for future erasure

    /// <summary>Gets or sets ClearedAtUtc.</summary>
    public DateTimeOffset? ClearedAtUtc { get; set; }

    // Zeeq 3-column vector pattern matching LibraryDocumentSnippet
    /// <summary>Gets or sets Embedding.</summary>
    public HalfVector? Embedding { get; set; }

    /// <summary>Gets or sets EmbeddingModel.</summary>
    public string? EmbeddingModel { get; set; }

    /// <summary>Gets or sets EmbeddingStartedAt.</summary>
    public DateTimeOffset? EmbeddingStartedAt { get; set; }
}
