using System.Linq.Expressions;
using Zeeq.Core.Auditing;

namespace Zeeq.Data.Postgres.Auditing;

/// <summary>Projects either audit stream into shared metadata or lazy detail rows in SQL.</summary>
/// <remarks>
/// Extend IQueryable, not a materialized event inside Select: an opaque entity mapping method can
/// cause EF to load the whole entity, including large snapshots. Each metadata mapping is an
/// expression tree reused by feeds and details. Snapshot composition substitutes parameters before
/// EF sees the tree; it uses neither Compile nor Invoke nor client-side entity materialization.
/// These helpers do not authorize callers. Apply tenant/team/system-surface scope before projection.
/// </remarks>
internal static class AuditEventQueryExtensions
{
    private static readonly Expression<
        Func<OrganizationAuditEvent, AuditRow>
    > OrganizationMetadata = e => new AuditRow
    {
        Id = e.Id,
        OccurredAtUtc = e.OccurredAtUtc,
        Scope = AuditScope.Organization,
        OrganizationId = e.OrganizationId,
        OrganizationLabel = e.OrganizationLabel,
        TeamId = e.TeamId,
        EventType = e.EventType,
        ChangeType = e.ChangeType,
        Area = e.Area,
        SubjectType = e.SubjectType,
        SubjectId = e.SubjectId,
        SubjectLabel = e.SubjectLabel,
        LibraryId = e.LibraryId,
        ActorKind = e.ActorKind,
        ActorId = e.ActorId,
        ActorDisplayName = e.ActorDisplayName,
        ActorChannel = e.ActorChannel,
        ActorResponsibleUserId = e.ActorResponsibleUserId,
        ActorCredentialId = e.ActorCredentialId,
        ActorCredentialName = e.ActorCredentialName,
        ActorInitiatedByUserId = e.ActorInitiatedByUserId,
        OperationId = e.OperationId,
        ChangedFields = e.ChangedFields,
        SourceReference = e.SourceReference,
        BeforeHash = e.BeforeHash,
        AfterHash = e.AfterHash,
        Outcome = e.Outcome,
        HasSnapshots = e.BeforeSnapshot != null || e.AfterSnapshot != null,
    };

    private static readonly Expression<Func<SystemAuditEvent, AuditRow>> SystemMetadata =
        e => new AuditRow
        {
            Id = e.Id,
            OccurredAtUtc = e.OccurredAtUtc,
            Scope = AuditScope.System,
            OrganizationLabel = e.OrganizationLabel,
            EventType = e.EventType,
            ChangeType = e.ChangeType,
            Area = e.Area,
            SubjectType = e.SubjectType,
            SubjectId = e.SubjectId,
            SubjectLabel = e.SubjectLabel,
            LibraryId = e.LibraryId,
            ActorKind = e.ActorKind,
            ActorId = e.ActorId,
            ActorDisplayName = e.ActorDisplayName,
            ActorChannel = e.ActorChannel,
            ActorResponsibleUserId = e.ActorResponsibleUserId,
            ActorCredentialId = e.ActorCredentialId,
            ActorCredentialName = e.ActorCredentialName,
            ActorInitiatedByUserId = e.ActorInitiatedByUserId,
            OperationId = e.OperationId,
            ChangedFields = e.ChangedFields,
            SourceReference = e.SourceReference,
            BeforeHash = e.BeforeHash,
            AfterHash = e.AfterHash,
            Outcome = e.Outcome,
            HasSnapshots = e.BeforeSnapshot != null || e.AfterSnapshot != null,
        };

    private static readonly Expression<
        Func<OrganizationAuditEvent, AuditDetailRow>
    > OrganizationDetail = WithSnapshots(
        OrganizationMetadata,
        (e, summary) => new AuditDetailRow(summary, e.BeforeSnapshot, e.AfterSnapshot)
    );

    private static readonly Expression<Func<SystemAuditEvent, AuditDetailRow>> SystemDetail =
        WithSnapshots(
            SystemMetadata,
            (e, summary) => new AuditDetailRow(summary, e.BeforeSnapshot, e.AfterSnapshot)
        );

    extension(IQueryable<OrganizationAuditEvent> events)
    {
        /// <summary>Projects tenant metadata without selecting snapshot values.</summary>
        public IQueryable<AuditRow> SelectAuditMetadata() => events.Select(OrganizationMetadata);

        /// <summary>Projects the same tenant metadata with both captured snapshot sides.</summary>
        public IQueryable<AuditDetailRow> SelectAuditDetails() => events.Select(OrganizationDetail);
    }

    extension(IQueryable<SystemAuditEvent> events)
    {
        /// <summary>Projects global metadata without selecting snapshot values.</summary>
        public IQueryable<AuditRow> SelectAuditMetadata() => events.Select(SystemMetadata);

        /// <summary>Projects the same global metadata with both captured snapshot sides.</summary>
        public IQueryable<AuditDetailRow> SelectAuditDetails() => events.Select(SystemDetail);
    }

    /// <summary>Inlines a metadata initializer into a detail selector so EF can translate one SQL projection.</summary>
    private static Expression<Func<TEvent, AuditDetailRow>> WithSnapshots<TEvent>(
        Expression<Func<TEvent, AuditRow>> metadata,
        Expression<Func<TEvent, AuditRow, AuditDetailRow>> detail
    )
    {
        var body = new ProjectionParameters(
            detail.Parameters[0],
            metadata.Parameters[0],
            detail.Parameters[1],
            metadata.Body
        ).Visit(detail.Body)!;
        return Expression.Lambda<Func<TEvent, AuditDetailRow>>(body, metadata.Parameters);
    }

    // Match parameters by identity, not name. Both event and summary must be rebound: leaving a
    // free parameter or an Invoke here would stop EF from translating the shared projection.
    private sealed class ProjectionParameters(
        ParameterExpression detailEvent,
        ParameterExpression metadataEvent,
        ParameterExpression detailSummary,
        Expression metadataBody
    ) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) =>
            node == detailEvent ? metadataEvent
            : node == detailSummary ? metadataBody
            : base.VisitParameter(node);
    }
}
