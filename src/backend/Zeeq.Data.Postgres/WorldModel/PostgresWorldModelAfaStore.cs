using System.Data.Common;
using System.Text.Json;
using Danom;
using Medallion.Threading.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Zeeq.Platform.WorldModel.Afa;

namespace Zeeq.Data.Postgres.WorldModel;

internal sealed class PostgresWorldModelAfaStore(PostgresDbContext db)
    : IWorldModelMutationStore,
        IWorldModelQueryStore
{
    public async Task<Result<WorldModelMutationBatchResult, string>> ApplyAsync(
        WorldModelMutationBatch batch,
        DateTimeOffset appliedAtUtc,
        CancellationToken cancellationToken
    )
    {
        if (batch.Validate() is { } error)
        {
            return Result<WorldModelMutationBatchResult, string>.Error(error);
        }

        try
        {
            // NOTE: An ambient transaction remains caller-owned; await using disposes the
            // transaction created here after commit or rollback.
            await using var transaction = await BeginTransactionIfNeededAsync(cancellationToken);
            var activeTransaction =
                db.Database.CurrentTransaction
                ?? throw new InvalidOperationException(
                    "AFA mutation application requires an active database transaction."
                );

            // AFA batches are intentionally serialized per organization. This makes hierarchy and
            // duplicate checks stable without blocking unrelated tenants.
            // NOTE: Caller cancellation bounds lock waiting; an arbitrary timeout could reject a
            // valid batch while an earlier batch for the same organization is still committing.
            await PostgresDistributedLock.AcquireWithTransactionAsync(
                new PostgresAdvisoryLockKey(
                    $"zeeq:world-model:afa-mutation:{batch.OrganizationId}",
                    allowHashing: true
                ),
                activeTransaction.GetDbTransaction(),
                timeout: null,
                cancellationToken
            );

            var outcomes = new Dictionary<string, WorldModelMutationOutcome>(
                StringComparer.Ordinal
            );
            // Persist each accepted operation inside the same transaction. Later operations can
            // resolve ancestors and body owners created earlier without exposing partial state.
            foreach (var mutation in OrderMutations(batch.Mutations))
            {
                outcomes[mutation.Reference] = await ApplyMutationAsync(
                    batch.OrganizationId,
                    mutation,
                    appliedAtUtc,
                    cancellationToken
                );
            }

            await CommitIfOwnedAsync(transaction, cancellationToken);
            var orderedOutcomes = batch
                .Mutations.Select(mutation => outcomes[mutation.Reference])
                .ToArray();

            return Result<WorldModelMutationBatchResult, string>.Ok(new(orderedOutcomes));
        }
        catch (Exception exception) when (exception is DbException or DbUpdateException)
        {
            return Result<WorldModelMutationBatchResult, string>.Error(exception.Message);
        }
    }

    public async Task<Result<WorldModelNode?, string>> FindNodeByPathAsync(
        string organizationId,
        WorldModelPath path,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var row = await db
                .WorldModelNodes.AsNoTracking()
                .TagWithOperationCallSite("world_model.afa.find_node_by_path")
                .SingleOrDefaultAsync(
                    node => node.OrganizationId == organizationId && node.Path == path.Value,
                    cancellationToken
                );

            return Result<WorldModelNode?, string>.Ok(row is null ? null : MapNode(row));
        }
        catch (Exception exception) when (exception is DbException or DbUpdateException)
        {
            return Result<WorldModelNode?, string>.Error(exception.Message);
        }
    }

    public async Task<Result<IReadOnlyList<WorldModelNode>, string>> ListChildrenAsync(
        string organizationId,
        Guid parentId,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var rows = await db
                .WorldModelNodes.AsNoTracking()
                .TagWithOperationCallSite("world_model.afa.list_children")
                .Where(node => node.OrganizationId == organizationId && node.ParentId == parentId)
                .OrderBy(node => node.Segment)
                .ToArrayAsync(cancellationToken);

            return Result<IReadOnlyList<WorldModelNode>, string>.Ok(rows.Select(MapNode).ToArray());
        }
        catch (Exception exception) when (exception is DbException or DbUpdateException)
        {
            return Result<IReadOnlyList<WorldModelNode>, string>.Error(exception.Message);
        }
    }

    public async Task<Result<WorldModelNodeContent?, string>> GetNodeContentAsync(
        string organizationId,
        Guid nodeId,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var node = await db
                .WorldModelNodes.AsNoTracking()
                .TagWithOperationCallSite("world_model.afa.get_node")
                .SingleOrDefaultAsync(
                    row => row.OrganizationId == organizationId && row.Id == nodeId,
                    cancellationToken
                );
            if (node is null)
            {
                return Result<WorldModelNodeContent?, string>.Ok(null);
            }

            var items = await db
                .WorldModelBodyItems.AsNoTracking()
                .TagWithOperationCallSite("world_model.afa.list_body_items")
                .Where(row => row.OrganizationId == organizationId && row.NodeId == nodeId)
                .OrderBy(row => row.Kind)
                .ThenBy(row => row.Name)
                .ThenBy(row => row.Id)
                .ToArrayAsync(cancellationToken);

            return Result<WorldModelNodeContent?, string>.Ok(
                new(MapNode(node), items.Select(MapBodyItem).ToArray())
            );
        }
        catch (Exception exception) when (exception is DbException or DbUpdateException)
        {
            return Result<WorldModelNodeContent?, string>.Error(exception.Message);
        }
    }

    private async Task<WorldModelMutationOutcome> ApplyMutationAsync(
        string organizationId,
        WorldModelMutation mutation,
        DateTimeOffset appliedAtUtc,
        CancellationToken cancellationToken
    )
    {
        var outcome = mutation switch
        {
            AddWorldModelNode add => await AddNodeAsync(
                organizationId,
                add,
                appliedAtUtc,
                cancellationToken
            ),
            UpdateWorldModelNode update => await UpdateNodeAsync(
                organizationId,
                update,
                appliedAtUtc,
                cancellationToken
            ),
            ObsoleteWorldModelNode obsolete => await ObsoleteNodeAsync(
                organizationId,
                obsolete,
                appliedAtUtc,
                cancellationToken
            ),
            AddWorldModelBodyItem add => await AddBodyItemAsync(
                organizationId,
                add,
                appliedAtUtc,
                cancellationToken
            ),
            UpdateWorldModelBodyItem update => await UpdateBodyItemAsync(
                organizationId,
                update,
                appliedAtUtc,
                cancellationToken
            ),
            ObsoleteWorldModelBodyItem obsolete => await ObsoleteBodyItemAsync(
                organizationId,
                obsolete,
                appliedAtUtc,
                cancellationToken
            ),
            _ => Rejected(
                mutation,
                WorldModelMutationErrorCode.Validation,
                "Unknown mutation type."
            ),
        };

        // NOTE: Applied operations flush immediately so later topological operations can query
        // them. Logical no-ops and rejections never have tracked changes to persist.
        if (outcome.Status == WorldModelMutationStatus.Applied)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return outcome;
    }

    private async Task<WorldModelMutationOutcome> AddNodeAsync(
        string organizationId,
        AddWorldModelNode mutation,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        var pathResult = WorldModelPath.Create(mutation.Path.Value);
        if (
            !pathResult.TryGet(out var path)
            || string.IsNullOrWhiteSpace(mutation.Description)
            || ExceedsMaxLength(mutation.TeamId, 128)
        )
        {
            return Rejected(
                mutation,
                WorldModelMutationErrorCode.Validation,
                "Node path and description are required, and team id cannot exceed 128 characters."
            );
        }

        if (await FindNodeRowByPathAsync(organizationId, path.Value, cancellationToken) is not null)
        {
            return Rejected(
                mutation,
                WorldModelMutationErrorCode.Duplicate,
                "A node already exists at this path."
            );
        }

        WorldModelNodeRow? parent = null;
        if (path.ParentPath is { } parentPath)
        {
            parent = await FindNodeRowByPathAsync(organizationId, parentPath, cancellationToken);
            if (
                parent is null
                || parent.IsEffectivelyObsolete
                || (int)parent.Kind + 1 != (int)path.Kind
            )
            {
                return Rejected(
                    mutation,
                    WorldModelMutationErrorCode.InvalidHierarchy,
                    "The active parent node was not found at the expected hierarchy level."
                );
            }
        }

        var row = new WorldModelNodeRow
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            TeamId = NormalizeOptional(mutation.TeamId),
            ParentId = parent?.Id,
            Kind = path.Kind,
            Segment = path.Segment,
            Path = path.Value,
            Description = mutation.Description.Trim(),
            IsEffectivelyObsolete = false,
            Version = 1,
            SemanticRevision = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        db.WorldModelNodes.Add(row);

        return Applied(mutation, row.Id, row.Version);
    }

    private async Task<WorldModelMutationOutcome> UpdateNodeAsync(
        string organizationId,
        UpdateWorldModelNode mutation,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        if (
            mutation.ExpectedVersion < 1
            || string.IsNullOrWhiteSpace(mutation.Description)
            || ExceedsMaxLength(mutation.TeamId, 128)
        )
        {
            return Rejected(
                mutation,
                WorldModelMutationErrorCode.Validation,
                "Expected version and node description are required, and team id cannot exceed 128 characters."
            );
        }

        var row = await FindNodeRowAsync(organizationId, mutation.NodeId, cancellationToken);
        if (row is null)
        {
            return Rejected(mutation, WorldModelMutationErrorCode.NotFound, "Node was not found.");
        }

        var teamId = NormalizeOptional(mutation.TeamId);
        var description = mutation.Description.Trim();
        if (row.IsEffectivelyObsolete)
        {
            return Rejected(
                mutation,
                WorldModelMutationErrorCode.ObsoleteTarget,
                "Node is obsolete.",
                row.Version
            );
        }

        if (row.Version != mutation.ExpectedVersion)
        {
            return row.TeamId == teamId && row.Description == description
                ? AlreadySatisfied(mutation, row.Id, row.Version)
                : Rejected(
                    mutation,
                    WorldModelMutationErrorCode.Conflict,
                    "Node version has changed.",
                    row.Version
                );
        }

        if (row.TeamId == teamId && row.Description == description)
        {
            return AlreadySatisfied(mutation, row.Id, row.Version);
        }

        row.TeamId = teamId;
        row.Description = description;
        row.Version++;
        row.SemanticRevision++;
        row.UpdatedAtUtc = now;
        return Applied(mutation, row.Id, row.Version);
    }

    private async Task<WorldModelMutationOutcome> ObsoleteNodeAsync(
        string organizationId,
        ObsoleteWorldModelNode mutation,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        if (
            mutation.ExpectedVersion < 1
            || string.IsNullOrWhiteSpace(mutation.Reason)
            || mutation.ReplacedByNodeId == mutation.NodeId
        )
        {
            return Rejected(
                mutation,
                WorldModelMutationErrorCode.Validation,
                "Expected version and obsolete reason are required, and a node cannot replace itself."
            );
        }

        var row = await FindNodeRowAsync(organizationId, mutation.NodeId, cancellationToken);
        if (row is null)
        {
            return Rejected(mutation, WorldModelMutationErrorCode.NotFound, "Node was not found.");
        }

        if (row.Obsolete is not null)
        {
            return ObsoleteOutcome(
                mutation,
                row.Id,
                row.Version,
                row.Obsolete,
                mutation.Reason,
                mutation.ReplacedByNodeId,
                "Node"
            );
        }

        if (row.IsEffectivelyObsolete)
        {
            return Rejected(
                mutation,
                WorldModelMutationErrorCode.ObsoleteTarget,
                "Node is already obsolete through an ancestor.",
                row.Version
            );
        }

        if (row.Version != mutation.ExpectedVersion)
        {
            return Rejected(
                mutation,
                WorldModelMutationErrorCode.Conflict,
                "Node version has changed.",
                row.Version
            );
        }

        if (mutation.ReplacedByNodeId is { } replacementId)
        {
            var replacement = await FindNodeRowAsync(
                organizationId,
                replacementId,
                cancellationToken
            );
            if (
                replacement is null
                || replacement.IsEffectivelyObsolete
                || replacement.Kind != row.Kind
            )
            {
                return Rejected(
                    mutation,
                    WorldModelMutationErrorCode.Validation,
                    "Replacement node must be an active node of the same kind."
                );
            }
        }

        row.Obsolete = SerializeObsolete(
            now,
            mutation.Reason,
            "replacedByNodeId",
            mutation.ReplacedByNodeId
        );
        row.IsEffectivelyObsolete = true;
        row.Version++;
        row.SemanticRevision++;
        row.UpdatedAtUtc = now;

        // Effective obsolescence is materialized so retrieval never has to walk ancestors.
        var descendants = await db
            .WorldModelNodes.Where(node =>
                node.OrganizationId == organizationId && node.Path.StartsWith(row.Path + ".")
            )
            .ToArrayAsync(cancellationToken);
        foreach (var descendant in descendants)
        {
            if (!descendant.IsEffectivelyObsolete)
            {
                descendant.IsEffectivelyObsolete = true;
                descendant.SemanticRevision++;
                descendant.UpdatedAtUtc = now;
            }
        }

        return Applied(mutation, row.Id, row.Version);
    }

    private async Task<WorldModelMutationOutcome> AddBodyItemAsync(
        string organizationId,
        AddWorldModelBodyItem mutation,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        var validation = ValidateBodyValues(
            mutation.Kind,
            mutation.Name,
            mutation.Content,
            mutation.Participants
        );
        if (
            validation is not null
            || mutation.ActionPath.Kind != WorldModelNodeKind.Action
            || ExceedsMaxLength(mutation.RepoPrSha, 128)
        )
        {
            return Rejected(
                mutation,
                WorldModelMutationErrorCode.Validation,
                validation
                    ?? "Body items may target Action paths only, and repository SHA cannot exceed 128 characters."
            );
        }

        var node = await FindNodeRowByPathAsync(
            organizationId,
            mutation.ActionPath.Value,
            cancellationToken
        );
        if (node is null || node.Kind != WorldModelNodeKind.Action)
        {
            return Rejected(
                mutation,
                WorldModelMutationErrorCode.InvalidHierarchy,
                "Action node was not found."
            );
        }

        if (node.IsEffectivelyObsolete)
        {
            return Rejected(
                mutation,
                WorldModelMutationErrorCode.ObsoleteTarget,
                "Action node is obsolete."
            );
        }

        var row = new WorldModelBodyItemRow
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            NodeId = node.Id,
            Kind = mutation.Kind,
            Name = mutation.Name.Trim(),
            Description = NormalizeOptional(mutation.Description),
            Content = mutation.Content.Trim(),
            Participants = NormalizeParticipants(mutation.Participants),
            Revision = await NextRevisionAsync(cancellationToken),
            RepoPrSha = NormalizeOptional(mutation.RepoPrSha),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        db.WorldModelBodyItems.Add(row);
        TouchSemanticRevision(node, now);
        return Applied(mutation, row.Id, row.Revision);
    }

    private async Task<WorldModelMutationOutcome> UpdateBodyItemAsync(
        string organizationId,
        UpdateWorldModelBodyItem mutation,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        var row = await FindBodyRowAsync(organizationId, mutation.ItemId, cancellationToken);
        if (row is null)
        {
            return Rejected(
                mutation,
                WorldModelMutationErrorCode.NotFound,
                "Body item was not found."
            );
        }

        var validation = ValidateBodyValues(
            row.Kind,
            mutation.Name,
            mutation.Content,
            mutation.Participants
        );
        if (
            mutation.ExpectedRevision < 1
            || validation is not null
            || ExceedsMaxLength(mutation.RepoPrSha, 128)
        )
        {
            return Rejected(
                mutation,
                WorldModelMutationErrorCode.Validation,
                validation
                    ?? "Expected revision is required, and repository SHA cannot exceed 128 characters.",
                row.Revision
            );
        }

        if (row.Obsolete is not null)
        {
            return Rejected(
                mutation,
                WorldModelMutationErrorCode.ObsoleteTarget,
                "Body item is obsolete.",
                row.Revision
            );
        }

        var owner = await FindBodyOwnerAsync(organizationId, row.NodeId, cancellationToken);
        if (owner.IsEffectivelyObsolete)
        {
            return Rejected(
                mutation,
                WorldModelMutationErrorCode.ObsoleteTarget,
                "Body item's owning Action is obsolete.",
                row.Revision
            );
        }

        var name = mutation.Name.Trim();
        var description = NormalizeOptional(mutation.Description);
        var content = mutation.Content.Trim();
        var participants = NormalizeParticipants(mutation.Participants);
        var repoPrSha = NormalizeOptional(mutation.RepoPrSha);
        var isSame =
            row.Name == name
            && row.Description == description
            && row.Content == content
            && row.Participants.SequenceEqual(participants, StringComparer.Ordinal)
            && row.RepoPrSha == repoPrSha;
        if (row.Revision != mutation.ExpectedRevision)
        {
            return isSame
                ? AlreadySatisfied(mutation, row.Id, row.Revision)
                : Rejected(
                    mutation,
                    WorldModelMutationErrorCode.Conflict,
                    "Body item revision has changed.",
                    row.Revision
                );
        }

        if (isSame)
        {
            return AlreadySatisfied(mutation, row.Id, row.Revision);
        }

        row.Name = name;
        row.Description = description;
        row.Content = content;
        row.Participants = participants;
        row.RepoPrSha = repoPrSha;
        row.Revision = await NextRevisionAsync(cancellationToken);
        row.UpdatedAtUtc = now;
        TouchSemanticRevision(owner, now);
        return Applied(mutation, row.Id, row.Revision);
    }

    private async Task<WorldModelMutationOutcome> ObsoleteBodyItemAsync(
        string organizationId,
        ObsoleteWorldModelBodyItem mutation,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        if (
            mutation.ExpectedRevision < 1
            || string.IsNullOrWhiteSpace(mutation.Reason)
            || mutation.ReplacedByItemId == mutation.ItemId
        )
        {
            return Rejected(
                mutation,
                WorldModelMutationErrorCode.Validation,
                "Expected revision and obsolete reason are required, and an item cannot replace itself."
            );
        }

        var row = await FindBodyRowAsync(organizationId, mutation.ItemId, cancellationToken);
        if (row is null)
        {
            return Rejected(
                mutation,
                WorldModelMutationErrorCode.NotFound,
                "Body item was not found."
            );
        }

        if (row.Obsolete is not null)
        {
            return ObsoleteOutcome(
                mutation,
                row.Id,
                row.Revision,
                row.Obsolete,
                mutation.Reason,
                mutation.ReplacedByItemId,
                "Body item"
            );
        }

        var owner = await FindBodyOwnerAsync(organizationId, row.NodeId, cancellationToken);
        if (owner.IsEffectivelyObsolete)
        {
            return Rejected(
                mutation,
                WorldModelMutationErrorCode.ObsoleteTarget,
                "Body item's owning Action is obsolete.",
                row.Revision
            );
        }

        if (row.Revision != mutation.ExpectedRevision)
        {
            return Rejected(
                mutation,
                WorldModelMutationErrorCode.Conflict,
                "Body item revision has changed.",
                row.Revision
            );
        }

        if (mutation.ReplacedByItemId is { } replacementId)
        {
            var replacement = await FindBodyRowAsync(
                organizationId,
                replacementId,
                cancellationToken
            );
            if (
                replacement is null
                || replacement.Obsolete is not null
                || replacement.Kind != row.Kind
            )
            {
                return Rejected(
                    mutation,
                    WorldModelMutationErrorCode.Validation,
                    "Replacement body item must be an active item of the same kind."
                );
            }

            var replacementOwner = await FindBodyOwnerAsync(
                organizationId,
                replacement.NodeId,
                cancellationToken
            );
            if (replacementOwner.IsEffectivelyObsolete)
            {
                return Rejected(
                    mutation,
                    WorldModelMutationErrorCode.Validation,
                    "Replacement body item must belong to an active Action."
                );
            }
        }

        row.Obsolete = SerializeObsolete(
            now,
            mutation.Reason,
            "replacedByItemId",
            mutation.ReplacedByItemId
        );
        row.Revision = await NextRevisionAsync(cancellationToken);
        row.UpdatedAtUtc = now;
        TouchSemanticRevision(owner, now);
        return Applied(mutation, row.Id, row.Revision);
    }

    private Task<WorldModelNodeRow?> FindNodeRowAsync(
        string organizationId,
        Guid nodeId,
        CancellationToken cancellationToken
    ) =>
        db.WorldModelNodes.SingleOrDefaultAsync(
            row => row.OrganizationId == organizationId && row.Id == nodeId,
            cancellationToken
        );

    private Task<WorldModelNodeRow?> FindNodeRowByPathAsync(
        string organizationId,
        string path,
        CancellationToken cancellationToken
    ) =>
        db.WorldModelNodes.SingleOrDefaultAsync(
            row => row.OrganizationId == organizationId && row.Path == path,
            cancellationToken
        );

    private Task<WorldModelBodyItemRow?> FindBodyRowAsync(
        string organizationId,
        Guid itemId,
        CancellationToken cancellationToken
    ) =>
        db.WorldModelBodyItems.SingleOrDefaultAsync(
            row => row.OrganizationId == organizationId && row.Id == itemId,
            cancellationToken
        );

    private async Task<WorldModelNodeRow> FindBodyOwnerAsync(
        string organizationId,
        Guid nodeId,
        CancellationToken cancellationToken
    )
    {
        return
            await FindNodeRowAsync(organizationId, nodeId, cancellationToken)
            ?? throw new InvalidOperationException("Body item's owning node was not found.");
    }

    private static void TouchSemanticRevision(WorldModelNodeRow node, DateTimeOffset now)
    {
        // Body changes leave the node's write-concurrency version alone, but invalidate any
        // semantic index derived from the node's complete content block.
        node.SemanticRevision++;
        node.UpdatedAtUtc = now;
    }

    // Body revisions share one sequence so a newer value always means a later durable mutation.
    // NOTE: Allocate explicitly because updates and obsoletions need a new revision too; a column
    // default would only cover inserts and would split revision semantics across write paths.
    private async Task<long> NextRevisionAsync(CancellationToken cancellationToken) =>
        await db
            .Database.SqlQuery<long>($"SELECT nextval('zeeq.awm_revision_seq') AS \"Value\"")
            .SingleAsync(cancellationToken);

    private async ValueTask<IDbContextTransaction?> BeginTransactionIfNeededAsync(
        CancellationToken cancellationToken
    ) =>
        db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;

    private static Task CommitIfOwnedAsync(
        IDbContextTransaction? transaction,
        CancellationToken cancellationToken
    ) => transaction?.CommitAsync(cancellationToken) ?? Task.CompletedTask;

    private static IEnumerable<WorldModelMutation> OrderMutations(
        IReadOnlyList<WorldModelMutation> mutations
    ) =>
        // Parent-first node adds make the batch independent of caller ordering. Body mutations
        // run only after the hierarchy is stable and can resolve their owning Action.
        mutations
            .OrderBy(mutation =>
                mutation switch
                {
                    AddWorldModelNode add => (int)add.Path.Kind,
                    UpdateWorldModelNode => 4,
                    ObsoleteWorldModelNode => 5,
                    AddWorldModelBodyItem => 6,
                    UpdateWorldModelBodyItem => 7,
                    ObsoleteWorldModelBodyItem => 8,
                    _ => 9,
                }
            )
            .ThenBy(mutation => mutation.Reference, StringComparer.Ordinal);

    private static string? ValidateBodyValues(
        WorldModelBodyKind kind,
        string name,
        string content,
        IReadOnlyList<string> participants
    )
    {
        if (kind is not (WorldModelBodyKind.Rule or WorldModelBodyKind.Flow))
        {
            return "Body item kind must be Rule or Flow.";
        }

        if (
            string.IsNullOrWhiteSpace(name)
            || name.Trim().Length > 256
            || string.IsNullOrWhiteSpace(content)
        )
        {
            return "Body item name and content are required, and name cannot exceed 256 characters.";
        }

        return participants.Any(string.IsNullOrWhiteSpace)
            ? "Body item participants cannot contain empty paths."
            : null;
    }

    private static string[] NormalizeParticipants(IReadOnlyList<string> participants) =>
        participants
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool ExceedsMaxLength(string? value, int maxLength) =>
        value?.Trim().Length > maxLength;

    private static JsonDocument SerializeObsolete(
        DateTimeOffset atUtc,
        string reason,
        string replacementProperty,
        Guid? replacedById
    ) =>
        JsonSerializer.SerializeToDocument(
            new Dictionary<string, object?>
            {
                ["atUtc"] = atUtc,
                ["reason"] = reason.Trim(),
                [replacementProperty] = replacedById,
            }
        );

    private static WorldModelNode MapNode(WorldModelNodeRow row)
    {
        var pathResult = WorldModelPath.Create(row.Path);
        if (!pathResult.TryGet(out var path))
        {
            throw new InvalidOperationException($"Persisted AFA path '{row.Path}' is invalid.");
        }

        return new(
            row.Id,
            row.OrganizationId,
            row.TeamId,
            row.ParentId,
            row.Kind,
            row.Segment,
            path,
            row.Description,
            DeserializeObsolete(row.Obsolete),
            row.IsEffectivelyObsolete,
            row.Version,
            row.SemanticRevision,
            row.CreatedAtUtc,
            row.UpdatedAtUtc
        );
    }

    private static WorldModelBodyItem MapBodyItem(WorldModelBodyItemRow row) =>
        new(
            row.Id,
            row.OrganizationId,
            row.NodeId,
            row.Kind,
            row.Name,
            row.Description,
            row.Content,
            row.Participants,
            DeserializeObsolete(row.Obsolete),
            row.Revision,
            row.RepoPrSha,
            row.CreatedAtUtc,
            row.UpdatedAtUtc
        );

    private static WorldModelObsoleteMetadata? DeserializeObsolete(JsonDocument? value)
    {
        if (value is null)
        {
            return null;
        }

        var root = value.RootElement;
        Guid? replacedById = null;
        foreach (var propertyName in new[] { "replacedByNodeId", "replacedByItemId" })
        {
            if (
                root.TryGetProperty(propertyName, out var replacement)
                && replacement.ValueKind != JsonValueKind.Null
            )
            {
                replacedById = replacement.GetGuid();
                break;
            }
        }

        return new(
            root.GetProperty("atUtc").GetDateTimeOffset(),
            root.GetProperty("reason").GetString()
                ?? throw new InvalidOperationException("Obsolete reason is missing."),
            replacedById
        );
    }

    private static WorldModelMutationOutcome ObsoleteOutcome(
        WorldModelMutation mutation,
        Guid id,
        long revision,
        JsonDocument persistedValue,
        string requestedReason,
        Guid? requestedReplacementId,
        string targetName
    )
    {
        var persisted = DeserializeObsolete(persistedValue)
            ?? throw new InvalidOperationException("Persisted obsolete metadata is missing.");

        return persisted.Reason == requestedReason.Trim()
            && persisted.ReplacedById == requestedReplacementId
            ? AlreadySatisfied(mutation, id, revision)
            : Rejected(
                mutation,
                WorldModelMutationErrorCode.Conflict,
                $"{targetName} was already obsoleted with different metadata.",
                revision
            );
    }

    private static WorldModelMutationOutcome Applied(
        WorldModelMutation mutation,
        Guid id,
        long revision
    ) =>
        new(
            mutation.Reference,
            WorldModelMutationStatus.Applied,
            id,
            revision,
            WorldModelMutationErrorCode.None,
            null
        );

    private static WorldModelMutationOutcome AlreadySatisfied(
        WorldModelMutation mutation,
        Guid id,
        long revision
    ) =>
        new(
            mutation.Reference,
            WorldModelMutationStatus.AlreadySatisfied,
            id,
            revision,
            WorldModelMutationErrorCode.None,
            null
        );

    private static WorldModelMutationOutcome Rejected(
        WorldModelMutation mutation,
        WorldModelMutationErrorCode code,
        string error,
        long? revision = null
    ) => new(mutation.Reference, WorldModelMutationStatus.Rejected, null, revision, code, error);
}
