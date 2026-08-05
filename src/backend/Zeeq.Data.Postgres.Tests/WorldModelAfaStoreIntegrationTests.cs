using Danom;
using Microsoft.EntityFrameworkCore;
using Zeeq.Data.Postgres.WorldModel;
using Zeeq.Platform.WorldModel.Afa;
using Zeeq.Testing;

namespace Zeeq.Data.Postgres.Tests;

/// <summary>
/// Exercises deterministic AFA mutation and query behavior against real PostgreSQL.
///
/// Run:
/// dotnet run --project src/backend/Zeeq.Data.Postgres.Tests --output detailed --disable-logo --treenode-filter "/*/*/WorldModelAfaStoreIntegrationTests/*"
/// </summary>
[Property("integration", "true")]
[Property("testcontainer", "true")]
[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class WorldModelAfaStoreIntegrationTests(PgDatabaseFixture postgres)
{
    private static readonly DateTimeOffset AppliedAtUtc = new DateTimeOffset(
        2026,
        8,
        5,
        18,
        30,
        0,
        TimeSpan.Zero
    ).TruncateToPostgresPrecision();

    [Test]
    public async Task ApplyAsync_OrdersHierarchyBeforeBodyAndReturnsCallerOrder()
    {
        await using var context = postgres.CreateContext();
        var store = new PostgresWorldModelAfaStore(context);
        var organizationId = NewOrganizationId();
        var area = await PathAsync("commerce");
        var feature = await PathAsync("commerce.checkout");
        var action = await PathAsync("commerce.checkout.submit_order");
        WorldModelMutation[] mutations =
        [
            new AddWorldModelBodyItem(
                "body",
                action,
                WorldModelBodyKind.Rule,
                "Requires payment",
                null,
                "Given payment is valid",
                ["src/payments.cs", "src/orders.cs", "src/payments.cs"],
                "abc123"
            ),
            new AddWorldModelNode("action", action, null, "Submits an order."),
            new AddWorldModelNode("feature", feature, "checkout", "Checkout capability."),
            new AddWorldModelNode("area", area, null, "Commerce capabilities."),
        ];

        var result = await ApplyAsync(store, organizationId, mutations);

        await Assert
            .That(
                result
                    .Outcomes.Select(outcome => outcome.Reference)
                    .SequenceEqual(mutations.Select(mutation => mutation.Reference))
            )
            .IsTrue();
        await Assert
            .That(result.Outcomes.All(outcome => outcome.Status == WorldModelMutationStatus.Applied))
            .IsTrue();

        var actionNode = await AssertOkAsync(
            await store.FindNodeByPathAsync(organizationId, action, CancellationToken.None)
        );
        var content = await AssertOkAsync(
            await store.GetNodeContentAsync(
                organizationId,
                actionNode!.Id,
                CancellationToken.None
            )
        );

        await Assert.That(content).IsNotNull();
        await Assert.That(content!.Node.Kind).IsEqualTo(WorldModelNodeKind.Action);
        await Assert.That(content.Node.SemanticRevision).IsEqualTo(2);
        await Assert.That(content.Node.CreatedAtUtc).IsEqualTo(AppliedAtUtc);
        await Assert.That(content.BodyItems).HasSingleItem();
        await Assert
            .That(
                content.BodyItems[0].Participants.SequenceEqual(
                    ["src/orders.cs", "src/payments.cs"]
                )
            )
            .IsTrue();
    }

    [Test]
    public async Task ApplyAsync_WithRejectedOperation_CommitsValidOperations()
    {
        await using var context = postgres.CreateContext();
        var store = new PostgresWorldModelAfaStore(context);
        var organizationId = NewOrganizationId();
        var area = await PathAsync("identity");

        var result = await ApplyAsync(
            store,
            organizationId,
            [
                new AddWorldModelBodyItem(
                    "invalid-body",
                    area,
                    WorldModelBodyKind.Rule,
                    "Invalid target",
                    null,
                    "Body content",
                    []
                ),
                new AddWorldModelNode("valid-area", area, null, "Identity capabilities."),
            ]
        );

        await Assert
            .That(result.Outcomes[0].Status)
            .IsEqualTo(WorldModelMutationStatus.Rejected);
        await Assert
            .That(result.Outcomes[0].ErrorCode)
            .IsEqualTo(WorldModelMutationErrorCode.Validation);
        await Assert
            .That(result.Outcomes[1].Status)
            .IsEqualTo(WorldModelMutationStatus.Applied);
        await Assert
            .That(await context.WorldModelNodes.CountAsync(row => row.OrganizationId == organizationId))
            .IsEqualTo(1);
    }

    [Test]
    public async Task ApplyAsync_WithStaleUpdate_DistinguishesAlreadySatisfiedFromConflict()
    {
        await using var context = postgres.CreateContext();
        var store = new PostgresWorldModelAfaStore(context);
        var organizationId = NewOrganizationId();
        var area = await PathAsync("documents");
        var added = await ApplyAsync(
            store,
            organizationId,
            [new AddWorldModelNode("add", area, null, "Document capabilities.")]
        );
        var nodeId = added.Outcomes.Single().DurableId!.Value;

        var updated = await ApplyAsync(
            store,
            organizationId,
            [new UpdateWorldModelNode("update", nodeId, 1, "docs", "Updated description.")]
        );
        var stale = await ApplyAsync(
            store,
            organizationId,
            [
                new UpdateWorldModelNode(
                    "same",
                    nodeId,
                    1,
                    "docs",
                    "Updated description."
                ),
                new UpdateWorldModelNode("different", nodeId, 1, null, "Another description."),
            ]
        );

        await Assert.That(updated.Outcomes[0].CurrentRevision).IsEqualTo(2);
        await Assert
            .That(stale.Outcomes[0].Status)
            .IsEqualTo(WorldModelMutationStatus.AlreadySatisfied);
        await Assert
            .That(stale.Outcomes[1].Status)
            .IsEqualTo(WorldModelMutationStatus.Rejected);
        await Assert
            .That(stale.Outcomes[1].ErrorCode)
            .IsEqualTo(WorldModelMutationErrorCode.Conflict);
        await Assert.That(stale.Outcomes[1].CurrentRevision).IsEqualTo(2);
    }

    [Test]
    public async Task BodyMutations_AdvanceSemanticRevisionWithoutChangingNodeVersion()
    {
        await using var context = postgres.CreateContext();
        var store = new PostgresWorldModelAfaStore(context);
        var organizationId = NewOrganizationId();
        var area = await PathAsync("agents");
        var feature = await PathAsync("agents.tools");
        var action = await PathAsync("agents.tools.invoke");
        var added = await ApplyAsync(
            store,
            organizationId,
            [
                new AddWorldModelNode("area", area, null, "Agent capabilities."),
                new AddWorldModelNode("feature", feature, null, "Agent tools."),
                new AddWorldModelNode("action", action, null, "Invokes a tool."),
                new AddWorldModelBodyItem(
                    "body",
                    action,
                    WorldModelBodyKind.Flow,
                    "Tool invocation",
                    null,
                    "flowchart LR; A-->B",
                    ["src/tools.cs"]
                ),
            ]
        );
        var actionId = added.Outcomes.Single(outcome => outcome.Reference == "action").DurableId!.Value;
        var bodyOutcome = added.Outcomes.Single(outcome => outcome.Reference == "body");

        var updated = await ApplyAsync(
            store,
            organizationId,
            [
                new UpdateWorldModelBodyItem(
                    "update-body",
                    bodyOutcome.DurableId!.Value,
                    bodyOutcome.CurrentRevision!.Value,
                    "Tool invocation",
                    "Updated flow",
                    "flowchart LR; A-->B; B-->C",
                    ["src/tools.cs", "src/runtime.cs"]
                ),
            ]
        );
        var updateOutcome = updated.Outcomes.Single();
        var obsoleted = await ApplyAsync(
            store,
            organizationId,
            [
                new ObsoleteWorldModelBodyItem(
                    "obsolete-body",
                    bodyOutcome.DurableId.Value,
                    updateOutcome.CurrentRevision!.Value,
                    "Flow replaced"
                ),
            ]
        );
        var retry = await ApplyAsync(
            store,
            organizationId,
            [
                new ObsoleteWorldModelBodyItem(
                    "retry-obsolete-body",
                    bodyOutcome.DurableId.Value,
                    updateOutcome.CurrentRevision.Value,
                    "Flow replaced"
                ),
                new ObsoleteWorldModelBodyItem(
                    "conflicting-obsolete-body",
                    bodyOutcome.DurableId.Value,
                    updateOutcome.CurrentRevision.Value,
                    "Different reason"
                ),
            ]
        );

        var content = await AssertOkAsync(
            await store.GetNodeContentAsync(organizationId, actionId, CancellationToken.None)
        );
        await Assert.That(content).IsNotNull();
        await Assert.That(content!.Node.Version).IsEqualTo(1);
        await Assert.That(content.Node.SemanticRevision).IsEqualTo(4);
        await Assert
            .That(content.BodyItems[0].Revision)
            .IsGreaterThan(updateOutcome.CurrentRevision.Value);
        await Assert.That(content.BodyItems[0].Obsolete).IsNotNull();
        await Assert.That(content.BodyItems[0].Obsolete!.AtUtc).IsEqualTo(AppliedAtUtc);
        await Assert
            .That(obsoleted.Outcomes[0].Status)
            .IsEqualTo(WorldModelMutationStatus.Applied);
        await Assert
            .That(retry.Outcomes[0].Status)
            .IsEqualTo(WorldModelMutationStatus.AlreadySatisfied);
        await Assert
            .That(retry.Outcomes[1].ErrorCode)
            .IsEqualTo(WorldModelMutationErrorCode.Conflict);
    }

    [Test]
    public async Task ObsoleteNodeAsync_MarksDescendantsAndKeepsOrganizationsIsolated()
    {
        await using var context = postgres.CreateContext();
        var store = new PostgresWorldModelAfaStore(context);
        var firstOrganizationId = NewOrganizationId();
        var secondOrganizationId = NewOrganizationId();
        var area = await PathAsync("reviews");
        var feature = await PathAsync("reviews.automation");
        var action = await PathAsync("reviews.automation.publish");
        WorldModelMutation[] hierarchy =
        [
            new AddWorldModelNode("area", area, null, "Review capabilities."),
            new AddWorldModelNode("feature", feature, null, "Review automation."),
            new AddWorldModelNode("action", action, null, "Publishes a review."),
        ];
        var first = await ApplyAsync(store, firstOrganizationId, hierarchy);
        await ApplyAsync(store, secondOrganizationId, hierarchy);
        var areaId = first.Outcomes.Single(outcome => outcome.Reference == "area").DurableId!.Value;

        var obsolete = await ApplyAsync(
            store,
            firstOrganizationId,
            [new ObsoleteWorldModelNode("obsolete", areaId, 1, "Replaced architecture")]
        );
        var retry = await ApplyAsync(
            store,
            firstOrganizationId,
            [
                new ObsoleteWorldModelNode(
                    "retry-obsolete",
                    areaId,
                    1,
                    "Replaced architecture"
                ),
                new ObsoleteWorldModelNode(
                    "conflicting-obsolete",
                    areaId,
                    1,
                    "Different reason"
                ),
                new ObsoleteWorldModelNode(
                    "inherited-obsolete",
                    first.Outcomes.Single(outcome => outcome.Reference == "feature").DurableId!.Value,
                    1,
                    "Direct removal"
                ),
            ]
        );

        await Assert
            .That(obsolete.Outcomes[0].Status)
            .IsEqualTo(WorldModelMutationStatus.Applied);
        await Assert
            .That(retry.Outcomes[0].Status)
            .IsEqualTo(WorldModelMutationStatus.AlreadySatisfied);
        await Assert
            .That(retry.Outcomes[1].ErrorCode)
            .IsEqualTo(WorldModelMutationErrorCode.Conflict);
        await Assert
            .That(retry.Outcomes[2].ErrorCode)
            .IsEqualTo(WorldModelMutationErrorCode.ObsoleteTarget);
        var firstRows = await context
            .WorldModelNodes.AsNoTracking()
            .Where(row => row.OrganizationId == firstOrganizationId)
            .ToArrayAsync();
        var secondRows = await context
            .WorldModelNodes.AsNoTracking()
            .Where(row => row.OrganizationId == secondOrganizationId)
            .ToArrayAsync();
        await Assert.That(firstRows.All(row => row.IsEffectivelyObsolete)).IsTrue();
        await Assert.That(secondRows.All(row => !row.IsEffectivelyObsolete)).IsTrue();

        var persistedArea = firstRows.Single(row => row.Id == areaId);
        await Assert
            .That(persistedArea.Obsolete!.RootElement.GetProperty("atUtc").GetDateTimeOffset())
            .IsEqualTo(AppliedAtUtc);
        await Assert
            .That(persistedArea.Obsolete.RootElement.TryGetProperty("replacedByNodeId", out _))
            .IsTrue();
    }

    [Test]
    public async Task BodyMutations_WhenOwningHierarchyIsObsolete_AreRejected()
    {
        await using var context = postgres.CreateContext();
        var store = new PostgresWorldModelAfaStore(context);
        var organizationId = NewOrganizationId();
        var area = await PathAsync("runtime");
        var feature = await PathAsync("runtime.dispatch");
        var action = await PathAsync("runtime.dispatch.execute");
        var added = await ApplyAsync(
            store,
            organizationId,
            [
                new AddWorldModelNode("area", area, null, "Runtime capabilities."),
                new AddWorldModelNode("feature", feature, null, "Runtime dispatch."),
                new AddWorldModelNode("action", action, null, "Executes dispatched work."),
                new AddWorldModelBodyItem(
                    "body",
                    action,
                    WorldModelBodyKind.Rule,
                    "Dispatch rule",
                    null,
                    "Given work is ready",
                    ["src/dispatch.cs"]
                ),
            ]
        );
        var areaId = added.Outcomes.Single(outcome => outcome.Reference == "area").DurableId!.Value;
        var body = added.Outcomes.Single(outcome => outcome.Reference == "body");
        await ApplyAsync(
            store,
            organizationId,
            [new ObsoleteWorldModelNode("obsolete-area", areaId, 1, "Runtime replaced")]
        );

        var rejected = await ApplyAsync(
            store,
            organizationId,
            [
                new UpdateWorldModelBodyItem(
                    "update-body",
                    body.DurableId!.Value,
                    body.CurrentRevision!.Value,
                    "Updated dispatch rule",
                    null,
                    "Given updated work is ready",
                    ["src/dispatch.cs"]
                ),
                new ObsoleteWorldModelBodyItem(
                    "obsolete-body",
                    body.DurableId.Value,
                    body.CurrentRevision.Value,
                    "Body replaced"
                ),
            ]
        );

        await Assert
            .That(rejected.Outcomes.All(outcome =>
                outcome.Status == WorldModelMutationStatus.Rejected
                && outcome.ErrorCode == WorldModelMutationErrorCode.ObsoleteTarget
            ))
            .IsTrue();
        var persistedBody = await context
            .WorldModelBodyItems.AsNoTracking()
            .SingleAsync(row => row.OrganizationId == organizationId && row.Id == body.DurableId);
        await Assert.That(persistedBody.Revision).IsEqualTo(body.CurrentRevision);
        await Assert.That(persistedBody.Obsolete).IsNull();
    }

    [Test]
    public async Task ApplyAsync_WithConcurrentDuplicateAdds_PersistsOneNode()
    {
        var organizationId = NewOrganizationId();
        var area = await PathAsync("telemetry");
        await using var firstContext = postgres.CreateContext();
        await using var secondContext = postgres.CreateContext();
        var firstStore = new PostgresWorldModelAfaStore(firstContext);
        var secondStore = new PostgresWorldModelAfaStore(secondContext);

        var results = await Task.WhenAll(
            ApplyAsync(
                firstStore,
                organizationId,
                [new AddWorldModelNode("first", area, null, "Telemetry capabilities.")]
            ),
            ApplyAsync(
                secondStore,
                organizationId,
                [new AddWorldModelNode("second", area, null, "Telemetry capabilities.")]
            )
        );

        var outcomes = results.SelectMany(result => result.Outcomes).ToArray();
        await Assert
            .That(outcomes.Count(outcome => outcome.Status == WorldModelMutationStatus.Applied))
            .IsEqualTo(1);
        await Assert
            .That(outcomes.Count(outcome => outcome.ErrorCode == WorldModelMutationErrorCode.Duplicate))
            .IsEqualTo(1);
        await using var verificationContext = postgres.CreateContext();
        await Assert
            .That(
                await verificationContext.WorldModelNodes.CountAsync(row =>
                    row.OrganizationId == organizationId
                )
            )
            .IsEqualTo(1);
    }

    private static async Task<WorldModelMutationBatchResult> ApplyAsync(
        PostgresWorldModelAfaStore store,
        string organizationId,
        IReadOnlyList<WorldModelMutation> mutations
    ) =>
        await AssertOkAsync(
            await store.ApplyAsync(
                new(organizationId, mutations),
                AppliedAtUtc,
                CancellationToken.None
            )
        );

    private static async Task<WorldModelPath> PathAsync(string value) =>
        await AssertOkAsync(WorldModelPath.Create(value));

    private static string NewOrganizationId() => $"org-afa-{Guid.CreateVersion7()}";

    private static async Task<T> AssertOkAsync<T>(Result<T, string> result)
    {
        if (result.TryGetError(out var error))
        {
            throw new InvalidOperationException(error);
        }

        await Assert.That(result.TryGet(out var value)).IsTrue();
        return value!;
    }
}
