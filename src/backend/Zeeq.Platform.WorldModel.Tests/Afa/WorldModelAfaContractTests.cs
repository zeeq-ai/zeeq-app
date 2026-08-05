using Zeeq.Platform.WorldModel.Afa;

namespace Zeeq.Platform.WorldModel.Tests.Afa;

public sealed class WorldModelAfaContractTests
{
    [Test]
    public async Task Enums_HaveStableNumericValues()
    {
        await Assert
            .That(Enum.GetValues<WorldModelNodeKind>().Select(value => (int)value).SequenceEqual([0, 1, 2, 3]))
            .IsTrue();
        await Assert
            .That(Enum.GetValues<WorldModelBodyKind>().Select(value => (int)value).SequenceEqual([0, 1, 2]))
            .IsTrue();
        await Assert
            .That(Enum.GetValues<WorldModelMutationStatus>().Select(value => (int)value).SequenceEqual([0, 1, 2, 3]))
            .IsTrue();
        await Assert
            .That(Enum.GetValues<WorldModelMutationErrorCode>().Select(value => (int)value).SequenceEqual([0, 1, 2, 3, 4, 5, 6]))
            .IsTrue();
    }

    [Test]
    public async Task Create_WithActionPath_DerivesHierarchyValues()
    {
        var result = WorldModelPath.Create("commerce.checkout.submit_order");

        await Assert.That(result.TryGet(out var path)).IsTrue();
        await Assert.That(path.Kind).IsEqualTo(WorldModelNodeKind.Action);
        await Assert.That(path.Segment).IsEqualTo("submit_order");
        await Assert.That(path.ParentPath).IsEqualTo("commerce.checkout");
    }

    [Test]
    public async Task Create_WithInvalidPath_ReturnsError()
    {
        var uppercase = WorldModelPath.Create("Commerce.checkout");
        var emptySegment = WorldModelPath.Create("commerce..checkout");
        var tooDeep = WorldModelPath.Create("one.two.three.four");

        await Assert.That(uppercase.TryGetError(out _)).IsTrue();
        await Assert.That(emptySegment.TryGetError(out _)).IsTrue();
        await Assert.That(tooDeep.TryGetError(out _)).IsTrue();
    }

    [Test]
    public async Task Validate_WithDuplicateReferences_ReturnsBatchError()
    {
        var area = WorldModelPath.Create("identity");
        await Assert.That(area.TryGet(out var path)).IsTrue();
        var batch = new WorldModelMutationBatch(
            "org-1",
            [
                new AddWorldModelNode("duplicate", path, null, "Identity."),
                new AddWorldModelNode("duplicate", path, null, "Identity."),
            ]
        );

        await Assert.That(batch.Validate()).IsEqualTo(
            "Mutation references must be non-empty and unique within the batch."
        );
    }
}
