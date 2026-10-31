namespace Zeeq.Core.Auditing;

/// <summary>Coarse change categories used by the history filter.</summary>
public enum AuditChangeType
{
    Added,
    Edited,
    Deleted,
}

/// <summary>Product areas whose durable changes can be recorded.</summary>
public enum AuditArea
{
    Content,
    ReviewConfiguration,
    Settings,
    Security,
}

/// <summary>The ownership scope of the changed object, independently of its actor.</summary>
public enum AuditScope
{
    Organization,
    System,
}

/// <summary>Trusted execution identity, with human ownership and credential identity kept separate.</summary>
public sealed record AuditActor(
    string Kind,
    string Id,
    string DisplayName,
    string Channel,
    string? ResponsibleUserId = null,
    string? CredentialId = null,
    string? CredentialName = null,
    string? InitiatedByUserId = null
)
{
    /// <summary>Creates an explicitly named background-service identity.</summary>
    public static AuditActor Worker(string service) => new("system", service, service, "worker");
}

/// <summary>Request or worker provenance shared by all writes in one operation.</summary>
public interface IAuditActorAccessor
{
    AuditActor Current { get; }
    Guid OperationId { get; }
    string? RequestId { get; }
}

/// <summary>Scoped provenance populated by the HTTP boundary or explicitly by a worker.</summary>
public sealed class AuditActorContext : IAuditActorAccessor
{
    public AuditActor Current { get; set; } = AuditActor.Worker("Zeeq background service");
    public Guid OperationId { get; set; } = Guid.CreateVersion7();
    public string? RequestId { get; set; }
}

/// <summary>
/// Independently readable, explicitly allowlisted state. Never project secrets or operational fields.
/// Content is available only for local documents; synchronized content uses source references instead.
/// </summary>
public sealed record AuditSnapshot
{
    public string? Path { get; init; }
    public string? Title { get; init; }
    public string? Content { get; init; }
    public IReadOnlyDictionary<string, string?> Fields { get; init; } =
        new Dictionary<string, string?>();
}

/// <summary>Semantic fact appended inside the transaction that applies the corresponding mutation.</summary>
public sealed record AuditChange
{
    public required string EventType { get; init; }
    public required AuditChangeType ChangeType { get; init; }
    public required AuditArea Area { get; init; }
    public required string SubjectType { get; init; }
    public required string SubjectId { get; init; }
    public required string SubjectLabel { get; init; }
    public string? OrganizationLabel { get; init; }
    public string? LibraryId { get; init; }
    public AuditSnapshot? Before { get; init; }
    public AuditSnapshot? After { get; init; }
    public IReadOnlyList<string> ChangedFields { get; init; } = [];
    public AuditActor? ActorOverride { get; init; }
    public Guid? OperationId { get; init; }
    public string? SourceReference { get; init; }
    public string? BeforeHash { get; init; }
    public string? AfterHash { get; init; }
    public string? Outcome { get; init; }
}

/// <summary>
/// Attaches facts to the caller's unit of work. Implementations must not save or commit independently.
/// The caller must persist the mutation and facts together, including set-based SQL write paths.
/// </summary>
public interface IAuditTrailWriter
{
    void AppendOrganization(string organizationId, string? teamId, AuditChange change);
    void AppendSystem(AuditChange change);
}

/// <summary>Metadata-only event projection. Snapshot content must never be loaded for this shape.</summary>
public sealed record AuditEventSummary
{
    public required Guid Id { get; init; }
    public required DateTimeOffset OccurredAtUtc { get; init; }
    public required AuditScope Scope { get; init; }
    public string? OrganizationId { get; init; }
    public string? OrganizationLabel { get; init; }
    public string? TeamId { get; init; }
    public required string EventType { get; init; }
    public required AuditChangeType ChangeType { get; init; }
    public required AuditArea Area { get; init; }
    public required string SubjectType { get; init; }
    public required string SubjectId { get; init; }
    public required string SubjectLabel { get; init; }
    public string? LibraryId { get; init; }
    public required AuditActor Actor { get; init; }
    public required Guid OperationId { get; init; }
    public IReadOnlyList<string> ChangedFields { get; init; } = [];
    public string? SourceReference { get; init; }
    public string? BeforeHash { get; init; }
    public string? AfterHash { get; init; }
    public string? Outcome { get; init; }
    public bool HasSnapshots { get; init; }
}

/// <summary>Lazy event detail, returned only after current authorization succeeds.</summary>
public sealed record AuditEventDetail(
    AuditEventSummary Event,
    AuditSnapshot? Before,
    AuditSnapshot? After
);

/// <summary>Bounded keyset query; a null lower bound means all retained history.</summary>
public sealed record AuditHistoryQuery
{
    public DateTimeOffset? FromUtc { get; init; }
    public DateTimeOffset? ToUtc { get; init; }
    public string? Cursor { get; init; }
    public int Limit { get; init; } = 50;
    public AuditChangeType? ChangeType { get; init; }
    public AuditArea? Area { get; init; }
    public string? ActorId { get; init; }
    public string? SubjectType { get; init; }
    public string? SubjectId { get; init; }
    public string? LibraryId { get; init; }
    public Guid? OperationId { get; init; }

    /// <summary>
    /// Authorization restriction set by the domain boundary for non-admin readers: only events whose
    /// library is in this set (currently accessible libraries) are returned. Null means unrestricted.
    /// </summary>
    public IReadOnlyCollection<string>? RestrictToLibraryIds { get; init; }
}

/// <summary>A page of entries with an opaque continuation cursor and honest capture scope.</summary>
public sealed record AuditHistoryPage(
    IReadOnlyList<AuditEventSummary> Entries,
    string? NextCursor,
    string Coverage
);

/// <summary>Read-side storage contract. Authorization belongs at the domain boundary as well as scope filters here.</summary>
public interface IAuditTrailReader
{
    Task<AuditHistoryPage> ListOrganizationAsync(
        string organizationId,
        string? teamId,
        AuditHistoryQuery query,
        CancellationToken ct
    );

    Task<AuditEventDetail?> GetOrganizationEventAsync(
        string organizationId,
        string? teamId,
        Guid eventId,
        DateTimeOffset occurredAtUtc,
        CancellationToken ct
    );

    Task<AuditHistoryPage> ListSystemAsync(AuditHistoryQuery query, CancellationToken ct);

    Task<AuditEventDetail?> GetSystemEventAsync(
        Guid eventId,
        DateTimeOffset occurredAtUtc,
        CancellationToken ct
    );
}
