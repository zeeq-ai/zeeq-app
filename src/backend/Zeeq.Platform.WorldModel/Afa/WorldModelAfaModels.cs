namespace Zeeq.Platform.WorldModel.Afa;

#pragma warning disable CS1591 // Positional record members mirror the documented persistence contract.

/// <summary>Records why and when a durable world-model element became obsolete.</summary>
public sealed record WorldModelObsoleteMetadata(
    DateTimeOffset AtUtc,
    string Reason,
    Guid? ReplacedById
);

/// <summary>A durable node in an organization's Area/Feature/Action hierarchy.</summary>
public sealed record WorldModelNode(
    Guid Id,
    string OrganizationId,
    string? TeamId,
    Guid? ParentId,
    WorldModelNodeKind Kind,
    string Segment,
    WorldModelPath Path,
    string? Description,
    WorldModelObsoleteMetadata? Obsolete,
    bool IsEffectivelyObsolete,
    long Version,
    long SemanticRevision,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc
);

/// <summary>A durable rule or flow attached to an Action node.</summary>
public sealed record WorldModelBodyItem(
    Guid Id,
    string OrganizationId,
    Guid NodeId,
    WorldModelBodyKind Kind,
    string Name,
    string? Description,
    string Content,
    IReadOnlyList<string> Participants,
    WorldModelObsoleteMetadata? Obsolete,
    long Revision,
    string? RepoPrSha,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc
);

/// <summary>Returns an AFA node together with its ordered body content.</summary>
public sealed record WorldModelNodeContent(
    WorldModelNode Node,
    IReadOnlyList<WorldModelBodyItem> BodyItems
);

#pragma warning restore CS1591
