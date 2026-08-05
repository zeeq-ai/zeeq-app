using Danom;

namespace Zeeq.Platform.WorldModel.Afa;

#pragma warning disable CS1591 // Positional result members form the mutation response contract.

/// <summary>The durable result of one independently validated mutation.</summary>
public sealed record WorldModelMutationOutcome(
    string Reference,
    WorldModelMutationStatus Status,
    Guid? DurableId,
    long? CurrentRevision,
    WorldModelMutationErrorCode ErrorCode,
    string? Error
);

/// <summary>Mutation outcomes returned in the caller's original order.</summary>
public sealed record WorldModelMutationBatchResult(
    IReadOnlyList<WorldModelMutationOutcome> Outcomes
);

#pragma warning restore CS1591

/// <summary>Applies typed world-model mutations in one durable transaction.</summary>
public interface IWorldModelMutationStore
{
    /// <summary>Applies valid mutations and reports logical rejections per operation.</summary>
    Task<Result<WorldModelMutationBatchResult, string>> ApplyAsync(
        WorldModelMutationBatch batch,
        DateTimeOffset appliedAtUtc,
        CancellationToken cancellationToken
    );
}

/// <summary>Reads organization-scoped AFA nodes and body content.</summary>
public interface IWorldModelQueryStore
{
    /// <summary>Finds a node by its canonical path.</summary>
    Task<Result<WorldModelNode?, string>> FindNodeByPathAsync(
        string organizationId,
        WorldModelPath path,
        CancellationToken cancellationToken
    );

    /// <summary>Lists a node's immediate children in stable segment order.</summary>
    Task<Result<IReadOnlyList<WorldModelNode>, string>> ListChildrenAsync(
        string organizationId,
        Guid parentId,
        CancellationToken cancellationToken
    );

    /// <summary>Gets a node and its body items in stable kind/name order.</summary>
    Task<Result<WorldModelNodeContent?, string>> GetNodeContentAsync(
        string organizationId,
        Guid nodeId,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Validates batch-level invariants before delegating deterministic application to the store.
/// </summary>
public sealed class WorldModelMutationService(
    IWorldModelMutationStore store,
    TimeProvider? timeProvider = null
)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>Applies a batch using the service clock for durable timestamps.</summary>
    public Task<Result<WorldModelMutationBatchResult, string>> ApplyAsync(
        WorldModelMutationBatch batch,
        CancellationToken cancellationToken
    )
    {
        if (batch.Validate() is { } error)
        {
            return Task.FromResult(
                Result<WorldModelMutationBatchResult, string>.Error(error)
            );
        }

        return store.ApplyAsync(batch, _timeProvider.GetUtcNow(), cancellationToken);
    }
}
