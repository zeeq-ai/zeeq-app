namespace Zeeq.Core.Auditing;

/// <summary>Coarse change categories used by the history filter.</summary>
/// <remarks>
/// Keep these separate from <see cref="AuditChange.EventType"/>: a semantic action such as key
/// revocation is an edit to a retained record, not necessarily a deletion.
/// </remarks>
public enum AuditChangeType
{
    /// <summary>A subject was created.</summary>
    Added,

    /// <summary>A retained subject changed, including disable or revoke actions.</summary>
    Edited,

    /// <summary>A subject was removed; its audit history remains independently readable.</summary>
    Deleted,
}

/// <summary>Product areas whose durable changes can be recorded.</summary>
public enum AuditArea
{
    /// <summary>Documents and other library content.</summary>
    Content,

    /// <summary>Configuration that controls code-review behavior.</summary>
    ReviewConfiguration,

    /// <summary>Organization or platform configuration.</summary>
    Settings,

    /// <summary>Membership, credentials, and other access controls.</summary>
    Security,
}

/// <summary>The ownership scope of the changed object, independently of its actor.</summary>
/// <remarks>
/// A system administrator changing a tenant object produces an organization event. The system
/// view can surface that same event; actor privilege does not require a duplicate global event.
/// An operation that also changes a global object can emit a separate, linked system fact.
/// </remarks>
public enum AuditScope
{
    /// <summary>The subject belongs to an organization.</summary>
    Organization,

    /// <summary>The subject is global, with no owning organization.</summary>
    System,
}

/// <summary>Trusted execution identity, with human ownership and credential identity kept separate.</summary>
/// <remarks>
/// Record the identity and authority actually used, not a human inferred from an API key name or
/// a sync request. For a client credential the actor is the credential; for a user token it is the
/// user. These event-time facts explain attribution and do not grant future readers access.
/// </remarks>
/// <param name="Kind">Identity/authority discriminator; see <see cref="AuditActorKinds"/>.</param>
/// <param name="Id">Stable actor identity. Its namespace depends on <paramref name="Kind"/>.</param>
/// <param name="DisplayName">Non-secret event-time label, retained if the actor is later renamed or deleted.</param>
/// <param name="Channel">Execution channel, separate from who acted; see <see cref="AuditChannels"/>.</param>
/// <param name="ResponsibleUserId">Human owner of the credential, if known; ownership does not prove that person initiated this action.</param>
/// <param name="CredentialId">Credential or user-token identifier, never its secret value.</param>
/// <param name="CredentialName">Non-secret event-time credential label for readable attribution.</param>
/// <param name="InitiatedByUserId">Explicitly known requester of delegated work; a requester is not the author of synced content.</param>
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
/// <remarks>
/// Correlation is separate from replay protection. Neither an operation ID nor an idempotency
/// header makes the mutation exactly-once. Producers must define their own retry contract.
/// </remarks>
public interface IAuditActorAccessor
{
    /// <summary>Trusted actor resolved at the HTTP boundary or supplied by a worker.</summary>
    AuditActor Current { get; }

    /// <summary>Groups related facts, including changes in both ownership scopes; not a uniqueness constraint.</summary>
    Guid OperationId { get; }

    /// <summary>Links a fact to request diagnostics; retries can have different request IDs.</summary>
    string? RequestId { get; }
}

/// <summary>Scoped provenance populated by the HTTP boundary or explicitly by a worker.</summary>
/// <remarks>
/// Workers should supply a named actor and a durable run identity when available. The fallback
/// records background work without inventing a human; a fresh scoped ID alone does not identify
/// separately committed children of the same sync run.
/// </remarks>
public sealed class AuditActorContext : IAuditActorAccessor
{
    /// <inheritdoc />
    public AuditActor Current { get; set; } = AuditActor.Worker("Zeeq background service");

    /// <inheritdoc />
    public Guid OperationId { get; set; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public string? RequestId { get; set; }
}

/// <summary>
/// Independently readable, explicitly allowlisted state. Never project secrets or operational fields.
/// Content is available only for local documents; synchronized content uses source references instead.
/// </summary>
/// <remarks>
/// Each captured side must stand alone. Delta chains or references to a live subject would make
/// deletion, later edits, or missing earlier events prevent comparison and copy/download.
/// Producers select fields explicitly; do not serialize an EF entity or configuration wholesale.
/// This contract does not imply generic Restore support.
/// </remarks>
public sealed record AuditSnapshot
{
    /// <summary>Path on this side of the change, so a rename preserves both names.</summary>
    public string? Path { get; init; }

    /// <summary>Allowlisted local-document title; synchronized-document facts omit it.</summary>
    public string? Title { get; init; }

    /// <summary>Full local-document text for comparison and copy/download; null for synchronized facts.</summary>
    public string? Content { get; init; }

    /// <summary>
    /// Allowlisted state with stable field names. Store readable values, not credential material,
    /// ciphertext, or secret hashes. A null value is distinct from an uncaptured field.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Fields { get; init; } =
        new Dictionary<string, string?>();
}

/// <summary>Semantic fact appended inside the transaction that applies the corresponding mutation.</summary>
/// <remarks>
/// Producers call the writer explicitly: an EF change interceptor cannot decide the business
/// action, the safe snapshot fields, or the outcome of set-based writes and cascades. Build the
/// preimage from database state protected against concurrent writes, not a caller-mutated entity.
/// Suppress real no-ops in the producer. This draft is not itself evidence of a committed change.
/// </remarks>
public sealed record AuditChange
{
    /// <summary>Stable semantic name, for example <c>document.renamed</c>; retain its meaning for old events.</summary>
    public required string EventType { get; init; }

    /// <summary>Coarse feed category, independent of the action-specific event name.</summary>
    public required AuditChangeType ChangeType { get; init; }

    /// <summary>Product area for filtering; it does not define read permissions.</summary>
    public required AuditArea Area { get; init; }

    /// <summary>Subject namespace, for example <c>document</c>, so IDs are interpreted with their type.</summary>
    public required string SubjectType { get; init; }

    /// <summary>Stable subject identity; history must not depend on a live subject row or its current name.</summary>
    public required string SubjectId { get; init; }

    /// <summary>Non-secret event-time name/path, so renamed and deleted subjects remain recognizable.</summary>
    public required string SubjectLabel { get; init; }

    /// <summary>Optional event-time organization label for administrative context; never an authorization key.</summary>
    public string? OrganizationLabel { get; init; }

    /// <summary>
    /// Library context for filtering and current-access checks, not a foreign key to a live library.
    /// Null means no library context; it does not make an event public or system-scoped.
    /// </summary>
    public string? LibraryId { get; init; }

    /// <summary>Captured preimage; normally absent for creation. Null does not mean an expired payload.</summary>
    public AuditSnapshot? Before { get; init; }

    /// <summary>Captured postimage; normally absent for deletion. Snapshot capture can also be omitted by event policy.</summary>
    public AuditSnapshot? After { get; init; }

    /// <summary>Field names only for metadata feeds; keep old/new values in the lazy snapshots.</summary>
    public IReadOnlyList<string> ChangedFields { get; init; } = [];

    /// <summary>Explicit trusted producer identity, such as a sync worker; never accept an override from request data.</summary>
    public AuditActor? ActorOverride { get; init; }

    /// <summary>Overrides scoped correlation for a known durable operation/run, including separately committed children.</summary>
    public Guid? OperationId { get; init; }

    /// <summary>
    /// Non-secret provenance, such as <c>sync-run:run_...</c>, an external ID, or a source URL.
    /// It need not be a navigable URL or proof that historical source text remains available.
    /// </summary>
    public string? SourceReference { get; init; }

    /// <summary>Optional content fingerprint of the preimage; not a signature, secret hash, or tamper-proof chain.</summary>
    public string? BeforeHash { get; init; }

    /// <summary>Optional content fingerprint of the postimage; it cannot recover text that was not captured.</summary>
    public string? AfterHash { get; init; }

    /// <summary>Explicit producer outcome when known; absence must not be interpreted as proof of external completion.</summary>
    public string? Outcome { get; init; }
}

/// <summary>
/// Attaches facts to the caller's unit of work. Implementations must not save or commit independently.
/// The caller must persist the mutation and facts together, including set-based SQL write paths.
/// </summary>
/// <remarks>
/// Use the same scoped unit of work as the domain store. Persisting through a separate context or
/// background queue could leave a successful mutation without its fact, or a fact for rolled-back
/// work. A producer that catches a failed save must also clean up its attached facts before retry.
/// Append-only is an application convention here, not protection from database administrators.
/// </remarks>
public interface IAuditTrailWriter
{
    /// <summary>Records a tenant-owned change, whoever acted. A null team means organization-shared scope.</summary>
    void AppendOrganization(string organizationId, string? teamId, AuditChange change);

    /// <summary>Records a global change; system-admin authority alone does not make a tenant change global.</summary>
    void AppendSystem(AuditChange change);
}

/// <summary>Metadata-only event projection. Snapshot content must never be loaded for this shape.</summary>
/// <remarks>
/// Feed rows stay small even when retained snapshots are large. Keep field values out of this
/// projection and load them through the separately authorized detail path. Labels are historical
/// context; the reader's current membership and library access still decide visibility.
/// </remarks>
public sealed record AuditEventSummary
{
    /// <summary>Event identity within its scope and timestamp; do not discard the other lookup key parts.</summary>
    public required Guid Id { get; init; }

    /// <summary>
    /// Writer-assigned UTC record time, not transaction commit time or an earlier content-authored time.
    /// Preserve the exact feed value for detail lookup; JavaScript Date loses PostgreSQL microseconds.
    /// </summary>
    public required DateTimeOffset OccurredAtUtc { get; init; }

    /// <summary>Ownership stream of the canonical event, including tenant events surfaced in the system view.</summary>
    public required AuditScope Scope { get; init; }

    /// <summary>Owning organization for tenant events; null for global events, never a tenant-query wildcard.</summary>
    public string? OrganizationId { get; init; }

    /// <inheritdoc cref="AuditChange.OrganizationLabel"/>
    public string? OrganizationLabel { get; init; }

    /// <summary>Event's team context; null denotes organization-shared scope, not unrestricted cross-tenant access.</summary>
    public string? TeamId { get; init; }

    /// <inheritdoc cref="AuditChange.EventType"/>
    public required string EventType { get; init; }

    /// <inheritdoc cref="AuditChange.ChangeType"/>
    public required AuditChangeType ChangeType { get; init; }

    /// <inheritdoc cref="AuditChange.Area"/>
    public required AuditArea Area { get; init; }

    /// <inheritdoc cref="AuditChange.SubjectType"/>
    public required string SubjectType { get; init; }

    /// <inheritdoc cref="AuditChange.SubjectId"/>
    public required string SubjectId { get; init; }

    /// <inheritdoc cref="AuditChange.SubjectLabel"/>
    public required string SubjectLabel { get; init; }

    /// <inheritdoc cref="AuditChange.LibraryId"/>
    public string? LibraryId { get; init; }

    /// <summary>Attribution captured with the event; it is not the actor's current profile or permission set.</summary>
    public required AuditActor Actor { get; init; }

    /// <summary>Correlation across facts, not proof that an operation is complete or that this page contains all its children.</summary>
    public required Guid OperationId { get; init; }

    /// <inheritdoc cref="AuditChange.ChangedFields"/>
    public IReadOnlyList<string> ChangedFields { get; init; } = [];

    /// <inheritdoc cref="AuditChange.SourceReference"/>
    public string? SourceReference { get; init; }

    /// <inheritdoc cref="AuditChange.BeforeHash"/>
    public string? BeforeHash { get; init; }

    /// <inheritdoc cref="AuditChange.AfterHash"/>
    public string? AfterHash { get; init; }

    /// <inheritdoc cref="AuditChange.Outcome"/>
    public string? Outcome { get; init; }

    /// <summary>At least one snapshot exists; content-free sync facts also qualify, so this does not promise recoverable text.</summary>
    public bool HasSnapshots { get; init; }
}

/// <summary>Lazy event detail, returned only after current authorization succeeds.</summary>
/// <remarks>Null sides mean absent/uncaptured state, not automatic expiry; history has no payload TTL.</remarks>
/// <param name="Event">The same metadata identity used to select the event from its feed.</param>
/// <param name="Before">Captured state before the change, if applicable and recorded.</param>
/// <param name="After">Captured state after the change, if applicable and recorded.</param>
public sealed record AuditEventDetail(
    AuditEventSummary Event,
    AuditSnapshot? Before,
    AuditSnapshot? After
);

/// <summary>Bounded keyset query; a null lower bound means all retained history.</summary>
/// <remarks>
/// The HTTP boundary applies the default last-90-days window; this storage query has no implicit
/// retention cutoff. A date filter enables partition pruning without deleting older history.
/// Continue a cursor with the same filters; it is a position, not an authorization token or a
/// frozen database snapshot.
/// </remarks>
public sealed record AuditHistoryQuery
{
    /// <summary>Inclusive UTC lower bound; null leaves older retained events eligible.</summary>
    public DateTimeOffset? FromUtc { get; init; }

    /// <summary>Inclusive UTC upper bound, independent of the opaque continuation cursor.</summary>
    public DateTimeOffset? ToUtc { get; init; }

    /// <summary>Opaque position in descending timestamp/UUID order; both parts prevent gaps at timestamp ties.</summary>
    public string? Cursor { get; init; }

    /// <summary>Page bound enforced by the reader, including all-time queries; never an unbounded export.</summary>
    public int Limit { get; init; } = 50;

    /// <summary>Optional coarse category filter.</summary>
    public AuditChangeType? ChangeType { get; init; }

    /// <summary>Optional product-area filter; does not grant access to that area.</summary>
    public AuditArea? Area { get; init; }

    /// <summary>Optional actor identity filter, not the responsible human owner of every credential.</summary>
    public string? ActorId { get; init; }

    /// <summary>Optional subject namespace; combine with the ID for a subject timeline.</summary>
    public string? SubjectType { get; init; }

    /// <summary>Optional stable subject identity, usable even after the live subject is deleted.</summary>
    public string? SubjectId { get; init; }

    /// <summary>Optional library context filter; it only narrows the caller's authorized scope.</summary>
    public string? LibraryId { get; init; }

    /// <summary>Loads related facts across pages; correlation alone does not establish a sync run's final outcome.</summary>
    public Guid? OperationId { get; init; }

    /// <summary>
    /// Authorization restriction set by the domain boundary for non-admin readers: only events whose
    /// library is in this set (currently accessible libraries) are returned. Null means unrestricted.
    /// </summary>
    /// <remarks>
    /// Never bind this from client input. Empty means no accessible libraries; null removes this
    /// restriction only, not organization/team filters. Events without library context are excluded
    /// for restricted readers so organization-wide security events do not leak through a null match.
    /// </remarks>
    public IReadOnlyCollection<string>? RestrictToLibraryIds { get; init; }
}

/// <summary>A page of entries with an opaque continuation cursor and honest capture scope.</summary>
/// <param name="Entries">Only the authorized metadata in this page, not the complete operation or run.</param>
/// <param name="NextCursor">Continuation position; null ends this filtered result, not all retained history.</param>
/// <param name="Coverage">Producer coverage notice; an empty feed is not proof that no uncaptured action occurred.</param>
public sealed record AuditHistoryPage(
    IReadOnlyList<AuditEventSummary> Entries,
    string? NextCursor,
    string Coverage
);

/// <summary>Read-side storage contract. Authorization belongs at the domain boundary as well as scope filters here.</summary>
/// <remarks>
/// Organization methods never fall back to the global stream. The boundary must resolve live
/// membership and pass the applicable team/library restrictions; detail callers must also check
/// current library access before returning a result. System methods require system-admin
/// authorization at their boundary and can include the same canonical tenant event.
/// </remarks>
public interface IAuditTrailReader
{
    /// <summary>Reads tenant metadata only; a null team removes that filter, not the organization boundary.</summary>
    Task<AuditHistoryPage> ListOrganizationAsync(
        string organizationId,
        string? teamId,
        AuditHistoryQuery query,
        CancellationToken ct
    );

    /// <summary>Loads tenant detail by exact partition timestamp and event ID; a miss returns null.</summary>
    /// <remarks>Require the exact feed timestamp, not a rounded display time, and apply current library access at the caller.</remarks>
    Task<AuditEventDetail?> GetOrganizationEventAsync(
        string organizationId,
        string? teamId,
        Guid eventId,
        DateTimeOffset occurredAtUtc,
        CancellationToken ct
    );

    /// <summary>Reads bounded metadata from global events and tenant events surfaced for system administration.</summary>
    Task<AuditHistoryPage> ListSystemAsync(AuditHistoryQuery query, CancellationToken ct);

    /// <summary>Loads global or surfaced tenant detail by exact timestamp and event ID; only for an authorized system caller.</summary>
    Task<AuditEventDetail?> GetSystemEventAsync(
        Guid eventId,
        DateTimeOffset occurredAtUtc,
        CancellationToken ct
    );
}
