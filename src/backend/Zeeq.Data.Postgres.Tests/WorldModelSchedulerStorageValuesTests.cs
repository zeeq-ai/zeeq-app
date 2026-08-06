using Zeeq.Core.Models;
using Zeeq.Data.Postgres.WorldModel;
using Zeeq.Platform.WorldModel.Scheduling;

namespace Zeeq.Data.Postgres.Tests;

public sealed class WorldModelSchedulerStorageValuesTests
{
    [Test]
    public async Task Format_UsesStablePersistedValues()
    {
        await Assert
            .That(WorldModelSchedulerStorageValues.Format(OrganizationTier.Default))
            .IsEqualTo("Default");
        await Assert
            .That(WorldModelSchedulerStorageValues.Format(OrganizationTier.Priority))
            .IsEqualTo("Priority");
        await Assert
            .That(WorldModelSchedulerStorageValues.Format(OrganizationTier.Low))
            .IsEqualTo("Low");
        await Assert
            .That(WorldModelSchedulerStorageValues.Format(WorldModelWorkConsumer.Curator))
            .IsEqualTo("Curator");
        await Assert
            .That(WorldModelSchedulerStorageValues.Format(WorldModelWorkConsumer.ClusterIndex))
            .IsEqualTo("ClusterIndex");
    }

    [Test]
    public async Task TryParse_RoundTripsPersistedValues()
    {
        await Assert
            .That(WorldModelSchedulerStorageValues.TryParseTier("Priority", out var tier))
            .IsTrue();
        await Assert.That(tier).IsEqualTo(OrganizationTier.Priority);
        await Assert
            .That(
                WorldModelSchedulerStorageValues.TryParseConsumer(
                    "ClusterIndex",
                    out var consumer
                )
            )
            .IsTrue();
        await Assert.That(consumer).IsEqualTo(WorldModelWorkConsumer.ClusterIndex);
    }

    [Test]
    public async Task TryParse_WithUnknownValue_ReturnsFalse()
    {
        await Assert
            .That(WorldModelSchedulerStorageValues.TryParseTier("priority", out _))
            .IsFalse();
        await Assert
            .That(WorldModelSchedulerStorageValues.TryParseConsumer("unknown", out _))
            .IsFalse();
    }
}
