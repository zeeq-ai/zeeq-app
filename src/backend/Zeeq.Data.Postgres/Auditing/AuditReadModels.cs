using Zeeq.Core.Auditing;

namespace Zeeq.Data.Postgres.Auditing;

/// <summary>Metadata-only projection shared by both event tables. Has no snapshot members.</summary>
internal sealed class AuditRow
{
    public Guid Id { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public AuditScope Scope { get; set; }
    public string? OrganizationId { get; set; }
    public string? OrganizationLabel { get; set; }
    public string? TeamId { get; set; }
    public string EventType { get; set; } = null!;
    public string ChangeType { get; set; } = null!;
    public string Area { get; set; } = null!;
    public string SubjectType { get; set; } = null!;
    public string SubjectId { get; set; } = null!;
    public string SubjectLabel { get; set; } = null!;
    public string? LibraryId { get; set; }
    public string ActorKind { get; set; } = null!;
    public string ActorId { get; set; } = null!;
    public string ActorDisplayName { get; set; } = null!;
    public string ActorChannel { get; set; } = null!;
    public string? ActorResponsibleUserId { get; set; }
    public string? ActorCredentialId { get; set; }
    public string? ActorCredentialName { get; set; }
    public string? ActorInitiatedByUserId { get; set; }
    public Guid OperationId { get; set; }
    public string[] ChangedFields { get; set; } = [];
    public string? SourceReference { get; set; }
    public string? BeforeHash { get; set; }
    public string? AfterHash { get; set; }
    public string? Outcome { get; set; }
    public bool HasSnapshots { get; set; }

    public AuditEventSummary ToSummary() =>
        new()
        {
            Id = Id,
            OccurredAtUtc = OccurredAtUtc,
            Scope = Scope,
            OrganizationId = OrganizationId,
            OrganizationLabel = OrganizationLabel,
            TeamId = TeamId,
            EventType = EventType,
            ChangeType = Enum.TryParse<AuditChangeType>(ChangeType, out var changeType)
                ? changeType
                : AuditChangeType.Edited,
            Area = Enum.TryParse<AuditArea>(Area, out var area) ? area : AuditArea.Settings,
            SubjectType = SubjectType,
            SubjectId = SubjectId,
            SubjectLabel = SubjectLabel,
            LibraryId = LibraryId,
            Actor = new AuditActor(
                ActorKind,
                ActorId,
                ActorDisplayName,
                ActorChannel,
                ActorResponsibleUserId,
                ActorCredentialId,
                ActorCredentialName,
                ActorInitiatedByUserId
            ),
            OperationId = OperationId,
            ChangedFields = ChangedFields,
            SourceReference = SourceReference,
            BeforeHash = BeforeHash,
            AfterHash = AfterHash,
            Outcome = Outcome,
            HasSnapshots = HasSnapshots,
        };
}

/// <summary>Detail-only SQL projection; snapshot values never enter the metadata row type.</summary>
/// <param name="Summary">Shared metadata projection for either ownership stream.</param>
/// <param name="BeforeSnapshot">Serialized preimage loaded only by an authorized detail path.</param>
/// <param name="AfterSnapshot">Serialized postimage loaded only by an authorized detail path.</param>
internal sealed record AuditDetailRow(
    AuditRow Summary,
    string? BeforeSnapshot,
    string? AfterSnapshot
)
{
    /// <summary>Deserializes snapshots after materialization; keep this conversion outside SQL expressions.</summary>
    public AuditEventDetail ToDetail() =>
        new(
            Summary.ToSummary(),
            AuditSnapshotSerializer.Deserialize(BeforeSnapshot),
            AuditSnapshotSerializer.Deserialize(AfterSnapshot)
        );
}
