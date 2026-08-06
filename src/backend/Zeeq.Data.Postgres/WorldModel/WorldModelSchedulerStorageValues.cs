using Zeeq.Core.Models;
using Zeeq.Platform.WorldModel.Scheduling;

namespace Zeeq.Data.Postgres.WorldModel;

/// <summary>
/// Defines the stable string representation used by scheduler rows and raw SQL parameters.
/// </summary>
/// <remarks>
/// NOTE: Explicit mappings keep persisted values stable across CLR enum renames and reject numeric
/// enum strings that <see cref="Enum.Parse(Type, string)"/> would otherwise accept.
/// </remarks>
internal static class WorldModelSchedulerStorageValues
{
    private const string DefaultTier = "Default";
    private const string PriorityTier = "Priority";
    private const string LowTier = "Low";
    private const string CuratorConsumer = "Curator";
    private const string ClusterIndexConsumer = "ClusterIndex";

    /// <summary>Formats a tier using its stable persisted value.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The tier is not supported.</exception>
    public static string Format(OrganizationTier tier) =>
        tier switch
        {
            OrganizationTier.Default => DefaultTier,
            OrganizationTier.Priority => PriorityTier,
            OrganizationTier.Low => LowTier,
            _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown tier."),
        };

    /// <summary>Formats a consumer using its stable persisted value.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The consumer is not supported.</exception>
    public static string Format(WorldModelWorkConsumer consumer) =>
        consumer switch
        {
            WorldModelWorkConsumer.Curator => CuratorConsumer,
            WorldModelWorkConsumer.ClusterIndex => ClusterIndexConsumer,
            _ =>
                throw new ArgumentOutOfRangeException(
                    nameof(consumer),
                    consumer,
                    "Unknown consumer."
                ),
        };

    /// <summary>Parses an exact persisted tier value for EF materialization.</summary>
    /// <exception cref="ArgumentException">The persisted value is unknown.</exception>
    public static OrganizationTier ParseTier(string value) =>
        TryParseTier(value, out var tier)
            ? tier
            : throw new ArgumentException("Unknown persisted scheduler tier.", nameof(value));

    /// <summary>Parses an exact persisted consumer value for EF materialization.</summary>
    /// <exception cref="ArgumentException">The persisted value is unknown.</exception>
    public static WorldModelWorkConsumer ParseConsumer(string value) =>
        TryParseConsumer(value, out var consumer)
            ? consumer
            : throw new ArgumentException("Unknown persisted scheduler consumer.", nameof(value));

    /// <summary>Attempts a case-sensitive parse of a persisted tier value.</summary>
    public static bool TryParseTier(string value, out OrganizationTier tier)
    {
        tier = value switch
        {
            DefaultTier => OrganizationTier.Default,
            PriorityTier => OrganizationTier.Priority,
            LowTier => OrganizationTier.Low,
            _ => default,
        };

        return value is DefaultTier or PriorityTier or LowTier;
    }

    /// <summary>Attempts a case-sensitive parse of a persisted consumer value.</summary>
    public static bool TryParseConsumer(string value, out WorldModelWorkConsumer consumer)
    {
        consumer = value switch
        {
            CuratorConsumer => WorldModelWorkConsumer.Curator,
            ClusterIndexConsumer => WorldModelWorkConsumer.ClusterIndex,
            _ => default,
        };

        return value is CuratorConsumer or ClusterIndexConsumer;
    }
}
