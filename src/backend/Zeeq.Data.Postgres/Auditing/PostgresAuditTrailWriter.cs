using Zeeq.Core.Auditing;
using Zeeq.Core.Common;

namespace Zeeq.Data.Postgres.Auditing;

/// <summary>
/// Attaches audit facts to the scoped <see cref="PostgresDbContext"/> change tracker.
/// </summary>
/// <remarks>
/// Never calls <c>SaveChanges</c>: the owning store commits the mutation and its facts in one
/// transaction. Stores that use <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> must still call
/// <c>SaveChangesAsync</c> inside the same transaction to persist the attached events.
/// A store constructed without DI may fall back to
/// <c>new PostgresAuditTrailWriter(db, new AuditActorContext())</c>, which records under an
/// explicit background actor; it never silently drops events.
/// Capture is explicit so producers can select semantic actions and safe fields. An EF interceptor
/// would miss set-based SQL/cascade semantics and cannot supply the producer's locked preimage.
/// This writer does not verify that a mutation occurred or suppress no-ops; the producer owns both.
/// </remarks>
internal sealed class PostgresAuditTrailWriter(
    PostgresDbContext db,
    IAuditActorAccessor actors,
    TimeProvider? timeProvider = null
) : IAuditTrailWriter
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public void AppendOrganization(string organizationId, string? teamId, AuditChange change)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        ArgumentNullException.ThrowIfNull(change);
        Validate(change);

        var actor = change.ActorOverride ?? actors.Current;
        // Ownership selects this table; actor authority only selects the extra system view.
        // Reuse this canonical row instead of duplicating every sysadmin action in both streams.
        var row = new OrganizationAuditEvent
        {
            OrganizationId = organizationId,
            TeamId = string.IsNullOrWhiteSpace(teamId) ? null : teamId,
            SurfaceInSystem = actor.Kind == AuditActorKinds.SystemAdmin,
        };
        Fill(row, change, actor);
        db.OrganizationAuditEvents.Add(row);
    }

    /// <inheritdoc />
    public void AppendSystem(AuditChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Validate(change);

        var row = new SystemAuditEvent();
        Fill(row, change, change.ActorOverride ?? actors.Current);
        db.SystemAuditEvents.Add(row);
    }

    private void Fill(OrganizationAuditEvent row, AuditChange change, AuditActor actor)
    {
        row.Id = Guid.CreateVersion7();
        row.OccurredAtUtc = Now();
        row.OrganizationLabel = Trim(change.OrganizationLabel);
        row.EventType = change.EventType;
        row.ChangeType = change.ChangeType.ToString();
        row.Area = change.Area.ToString();
        row.SubjectType = change.SubjectType;
        row.SubjectId = change.SubjectId;
        row.SubjectLabel = Trim(change.SubjectLabel)!;
        row.LibraryId = change.LibraryId;
        row.ActorKind = actor.Kind;
        row.ActorId = actor.Id;
        row.ActorDisplayName = Trim(actor.DisplayName)!;
        row.ActorChannel = actor.Channel;
        row.ActorResponsibleUserId = actor.ResponsibleUserId;
        row.ActorCredentialId = actor.CredentialId;
        row.ActorCredentialName = Trim(actor.CredentialName);
        row.ActorInitiatedByUserId = actor.InitiatedByUserId;
        row.OperationId = change.OperationId ?? actors.OperationId;
        row.RequestId = actors.RequestId;
        row.ChangedFields = [.. change.ChangedFields];
        row.SourceReference = change.SourceReference;
        row.BeforeHash = change.BeforeHash;
        row.AfterHash = change.AfterHash;
        row.Outcome = change.Outcome;
        row.BeforeSnapshot = AuditSnapshotSerializer.Serialize(change.Before);
        row.AfterSnapshot = AuditSnapshotSerializer.Serialize(change.After);
    }

    private void Fill(SystemAuditEvent row, AuditChange change, AuditActor actor)
    {
        row.Id = Guid.CreateVersion7();
        row.OccurredAtUtc = Now();
        row.OrganizationLabel = Trim(change.OrganizationLabel);
        row.EventType = change.EventType;
        row.ChangeType = change.ChangeType.ToString();
        row.Area = change.Area.ToString();
        row.SubjectType = change.SubjectType;
        row.SubjectId = change.SubjectId;
        row.SubjectLabel = Trim(change.SubjectLabel)!;
        row.LibraryId = change.LibraryId;
        row.ActorKind = actor.Kind;
        row.ActorId = actor.Id;
        row.ActorDisplayName = Trim(actor.DisplayName)!;
        row.ActorChannel = actor.Channel;
        row.ActorResponsibleUserId = actor.ResponsibleUserId;
        row.ActorCredentialId = actor.CredentialId;
        row.ActorCredentialName = Trim(actor.CredentialName);
        row.ActorInitiatedByUserId = actor.InitiatedByUserId;
        row.OperationId = change.OperationId ?? actors.OperationId;
        row.RequestId = actors.RequestId;
        row.ChangedFields = [.. change.ChangedFields];
        row.SourceReference = change.SourceReference;
        row.BeforeHash = change.BeforeHash;
        row.AfterHash = change.AfterHash;
        row.Outcome = change.Outcome;
        row.BeforeSnapshot = AuditSnapshotSerializer.Serialize(change.Before);
        row.AfterSnapshot = AuditSnapshotSerializer.Serialize(change.After);
    }

    // Microsecond precision so the timestamp round-trips exactly as the detail lookup key.
    private DateTimeOffset Now() =>
        PostgresTimestampPrecision.TruncateToMicroseconds(_time.GetUtcNow());

    private static string? Trim(string? value) =>
        value is { Length: > AuditLimits.MaxLabelLength }
            ? string.Concat(value.AsSpan(0, AuditLimits.MaxLabelLength - 1), "…")
            : value;

    private static void Validate(AuditChange change)
    {
        Require(change.EventType, nameof(change.EventType), 128);
        Require(change.SubjectType, nameof(change.SubjectType), 64);
        Require(change.SubjectId, nameof(change.SubjectId), AuditLimits.MaxIdentifierLength);
        Require(change.SubjectLabel, nameof(change.SubjectLabel), int.MaxValue);

        if (change.ChangedFields.Count > AuditLimits.MaxChangedFields)
        {
            throw new ArgumentException(
                $"At most {AuditLimits.MaxChangedFields} changed fields can be recorded.",
                nameof(change)
            );
        }

        if (!Enum.IsDefined(change.ChangeType) || !Enum.IsDefined(change.Area))
        {
            throw new ArgumentException("Undefined audit change type or area.", nameof(change));
        }
    }

    private static void Require(string value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
        {
            throw new ArgumentException(
                $"{name} is required and must be at most {maxLength} characters."
            );
        }
    }
}
