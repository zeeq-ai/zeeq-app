namespace Zeeq.Platform.WorldModel.Afa;

/// <summary>
/// Identifies a node's fixed level in the Area/Feature/Action hierarchy.
/// </summary>
public enum WorldModelNodeKind
{
    /// <summary>An uninitialized or unsupported node kind.</summary>
    Unknown = 0,

    /// <summary>A top-level area.</summary>
    Area = 1,

    /// <summary>A feature beneath an area.</summary>
    Feature = 2,

    /// <summary>An action beneath a feature.</summary>
    Action = 3,
}

/// <summary>
/// Identifies the supported content attached to an Action node.
/// </summary>
public enum WorldModelBodyKind
{
    /// <summary>An uninitialized or unsupported body kind.</summary>
    Unknown = 0,

    /// <summary>A behavioral rule.</summary>
    Rule = 1,

    /// <summary>A flow definition.</summary>
    Flow = 2,
}

/// <summary>
/// Describes the deterministic result of one mutation in a batch.
/// </summary>
public enum WorldModelMutationStatus
{
    /// <summary>An uninitialized or unsupported status.</summary>
    Unknown = 0,

    /// <summary>The mutation changed durable state.</summary>
    Applied = 1,

    /// <summary>The durable state already matched the requested result.</summary>
    AlreadySatisfied = 2,

    /// <summary>The mutation failed validation without invalidating the batch.</summary>
    Rejected = 3,
}

/// <summary>
/// Categorizes a rejected mutation without exposing persistence exceptions as logical outcomes.
/// </summary>
public enum WorldModelMutationErrorCode
{
    /// <summary>No logical error occurred.</summary>
    None = 0,

    /// <summary>The mutation payload is invalid.</summary>
    Validation = 1,

    /// <summary>The durable target does not exist in the organization.</summary>
    NotFound = 2,

    /// <summary>The supplied optimistic concurrency value is stale.</summary>
    Conflict = 3,

    /// <summary>The mutation would create a duplicate durable target.</summary>
    Duplicate = 4,

    /// <summary>The mutation violates Area/Feature/Action hierarchy rules.</summary>
    InvalidHierarchy = 5,

    /// <summary>The target or one of its ancestors is obsolete.</summary>
    ObsoleteTarget = 6,
}
