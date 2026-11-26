using Pgvector;

namespace Zeeq.Core.Models;

/// <summary>A requested triage transition.</summary>
public enum FindingTriageOperation
{
    /// <summary>Set an active disposition.</summary>
    Set,

    /// <summary>Confirm a proposal.</summary>
    Confirm,

    /// <summary>Clear the decision.</summary>
    Clear,
}

/// <summary>Inputs to a row-locked triage transition. Snapshot identifies the occurrence and supplies its immutable metadata on creation.</summary>
public sealed record FindingTriageWrite(
    CodeReviewFindingTriage Snapshot,
    FindingTriageOperation Operation,
    CodeReviewFindingDisposition? Disposition,
    string? Reason,
    CodeReviewFindingTriageChannel Channel,
    string? ActorUserId,
    DateTimeOffset? SourceEventAtUtc = null,
    CodeReviewFindingDisposition? ExpectedDisposition = null,
    string? AgentSessionId = null,
    string? SourceUrl = null,
    string? ConfirmationQuote = null,
    string? AuthMode = null,
    string? ClientId = null
);

/// <summary>Result of a guarded storage transition.</summary>
public enum FindingTriageWriteStatus
{
    /// <summary>Applied or already identical.</summary>
    Applied,

    /// <summary>Caller lacks authority.</summary>
    Forbidden,

    /// <summary>Source event predates the watermark.</summary>
    IgnoredStale,

    /// <summary>Transition is invalid.</summary>
    Invalid,
}

/// <summary>Result and current persisted row, if any.</summary>
public sealed record FindingTriageWriteResult(
    FindingTriageWriteStatus Status,
    CodeReviewFindingTriage? Row,
    string? Error = null,
    bool Changed = false,
    CodeReviewFindingDisposition? PreviousDisposition = null,
    CodeReviewFindingApprovalState? PreviousApprovalState = null
);

/// <summary>A lease token for an embedding task. Both timestamps must be echoed when completing or releasing the lease.</summary>
public sealed record FindingTriageEmbeddingClaim(
    CodeReviewFindingTriage Row,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset StartedAtUtc
);

/// <summary>Similarity search result before optional presentation boosts.</summary>
public sealed record FindingTriageMatch(CodeReviewFindingTriage Row, double Distance);

/// <summary>Tenant-scoped storage for triage state and false-positive memory.</summary>
public interface ICodeReviewFindingTriageStore
{
    /// <summary>Apply a guarded transition under a row lock.</summary>
    Task<FindingTriageWriteResult> UpsertLwwAsync(
        FindingTriageWrite write,
        CancellationToken cancellationToken = default
    );

    /// <summary>Find one triage by tenant and ID.</summary>
    Task<CodeReviewFindingTriage?> FindAsync(
        string organizationId,
        string triageId,
        CancellationToken cancellationToken = default
    );

    /// <summary>List triages for a review.</summary>
    Task<IReadOnlyList<CodeReviewFindingTriage>> ListForReviewAsync(
        string organizationId,
        string reviewId,
        CancellationToken cancellationToken = default
    );

    /// <summary>List triages, including cleared tombstones, for the exact scope.</summary>
    Task<IReadOnlyList<CodeReviewFindingTriage>> ListForScopeAsync(
        string organizationId,
        string? repositoryId,
        string repoNameLower,
        int? prNumber,
        string? reviewGroupId,
        string reviewId,
        CancellationToken cancellationToken = default
    );

    /// <summary>Claim pending embedding work with expiring leases.</summary>
    Task<IReadOnlyList<FindingTriageEmbeddingClaim>> ClaimPendingEmbeddingsAsync(
        string targetModel,
        TimeSpan leaseDuration,
        int batchSize,
        CancellationToken cancellationToken = default
    );

    /// <summary>Complete a lease only if its token still matches.</summary>
    Task<bool> TryCompleteEmbeddingAsync(
        string organizationId,
        string id,
        DateTimeOffset updatedAtUtc,
        DateTimeOffset startedAtUtc,
        HalfVector vector,
        string model,
        CancellationToken cancellationToken = default
    );

    /// <summary>Release a lease only if its token still matches.</summary>
    Task<bool> ReleaseEmbeddingClaimAsync(
        string organizationId,
        string id,
        DateTimeOffset updatedAtUtc,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default
    );

    /// <summary>Search confirmed false positives in a repository.</summary>
    Task<IReadOnlyList<FindingTriageMatch>> SearchConfirmedFalsePositivesAsync(
        string organizationId,
        string? repositoryId,
        string repoNameLower,
        HalfVector vector,
        string model,
        double threshold,
        int limit,
        int? activePrNumber = null,
        string? activeReviewGroupId = null,
        string? activeReviewId = null,
        DateTimeOffset? asOfUtc = null,
        int? excludedPrNumber = null,
        CancellationToken cancellationToken = default
    );
}
