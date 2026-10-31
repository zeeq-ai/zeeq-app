// Row models mirror column names one-to-one; the envelope is documented on the contract types.
#pragma warning disable CS1591

namespace Zeeq.Data.Postgres.Auditing;

/// <summary>
/// Immutable fact about a tenant-owned change. Partitioned monthly by <see cref="OccurredAtUtc"/>.
/// </summary>
/// <remarks>
/// Deliberately has no foreign keys: the subject or actor may later be deleted or renamed, and
/// history must keep the event-time labels. Snapshot columns are jsonb and must only be selected
/// by the detail query. The system table keeps the same envelope but is a separate,
/// non-inheriting model so a tenant query can never reach a global fact.
/// </remarks>
public class OrganizationAuditEvent
{
    public Guid Id { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public string OrganizationId { get; set; } = null!;
    public string? TeamId { get; set; }
    public string? OrganizationLabel { get; set; }

    /// <summary>True when the same canonical event is also listed in the system view.</summary>
    public bool SurfaceInSystem { get; set; }

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

    /// <summary>Storage/snapshot format version; bump when the recorded shape changes.</summary>
    public int SchemaVersion { get; set; } = 1;

    public Guid OperationId { get; set; }
    public string? RequestId { get; set; }
    public string[] ChangedFields { get; set; } = [];
    public string? SourceReference { get; set; }
    public string? BeforeHash { get; set; }
    public string? AfterHash { get; set; }
    public string? Outcome { get; set; }

    /// <summary>jsonb; never selected by feed queries.</summary>
    public string? BeforeSnapshot { get; set; }

    /// <summary>jsonb; never selected by feed queries.</summary>
    public string? AfterSnapshot { get; set; }
}

/// <summary>Immutable fact about a global (non-tenant) change. Partitioned monthly by <see cref="OccurredAtUtc"/>.</summary>
public class SystemAuditEvent
{
    public Guid Id { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public string? OrganizationLabel { get; set; }
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

    /// <summary>Storage/snapshot format version; bump when the recorded shape changes.</summary>
    public int SchemaVersion { get; set; } = 1;

    public Guid OperationId { get; set; }
    public string? RequestId { get; set; }
    public string[] ChangedFields { get; set; } = [];
    public string? SourceReference { get; set; }
    public string? BeforeHash { get; set; }
    public string? AfterHash { get; set; }
    public string? Outcome { get; set; }
    public string? BeforeSnapshot { get; set; }
    public string? AfterSnapshot { get; set; }
}
