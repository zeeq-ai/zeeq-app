namespace Zeeq.Platform.WorldModel.Afa;

#pragma warning disable CS1591 // Positional record members are the typed mutation payload.

/// <summary>An organization-scoped set of independently validated mutations.</summary>
public sealed record WorldModelMutationBatch(
    string OrganizationId,
    IReadOnlyList<WorldModelMutation> Mutations
)
{
    /// <summary>Returns a batch-level validation error, or <see langword="null"/> when valid.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(OrganizationId) || OrganizationId.Length > 128)
        {
            return "Organization id is required and cannot exceed 128 characters.";
        }

        return Mutations.Any(mutation => string.IsNullOrWhiteSpace(mutation.Reference))
            || Mutations
                .Select(mutation => mutation.Reference)
                .Distinct(StringComparer.Ordinal)
                .Count() != Mutations.Count
            ? "Mutation references must be non-empty and unique within the batch."
            : null;
    }
}

/// <summary>A typed durable-state request correlated by a caller-provided reference.</summary>
public abstract record WorldModelMutation(string Reference);

/// <summary>Adds a node after its active parent exists.</summary>
public sealed record AddWorldModelNode(
    string Reference,
    WorldModelPath Path,
    string? TeamId,
    string Description
) : WorldModelMutation(Reference);

/// <summary>Updates mutable node fields using optimistic concurrency.</summary>
public sealed record UpdateWorldModelNode(
    string Reference,
    Guid NodeId,
    long ExpectedVersion,
    string? TeamId,
    string Description
) : WorldModelMutation(Reference);

/// <summary>Soft-deletes a node and makes every descendant effectively obsolete.</summary>
public sealed record ObsoleteWorldModelNode(
    string Reference,
    Guid NodeId,
    long ExpectedVersion,
    string Reason,
    Guid? ReplacedByNodeId = null
) : WorldModelMutation(Reference);

/// <summary>Adds a rule or flow to an active Action node.</summary>
public sealed record AddWorldModelBodyItem(
    string Reference,
    WorldModelPath ActionPath,
    WorldModelBodyKind Kind,
    string Name,
    string? Description,
    string Content,
    IReadOnlyList<string> Participants,
    string? RepoPrSha = null
) : WorldModelMutation(Reference);

/// <summary>Updates a body item using its global revision as the concurrency token.</summary>
public sealed record UpdateWorldModelBodyItem(
    string Reference,
    Guid ItemId,
    long ExpectedRevision,
    string Name,
    string? Description,
    string Content,
    IReadOnlyList<string> Participants,
    string? RepoPrSha = null
) : WorldModelMutation(Reference);

/// <summary>Soft-deletes a body item without removing its audit history.</summary>
public sealed record ObsoleteWorldModelBodyItem(
    string Reference,
    Guid ItemId,
    long ExpectedRevision,
    string Reason,
    Guid? ReplacedByItemId = null
) : WorldModelMutation(Reference);

#pragma warning restore CS1591
