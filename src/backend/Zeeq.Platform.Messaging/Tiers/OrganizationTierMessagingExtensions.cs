using Zeeq.Core.Models;

namespace Zeeq.Platform.Messaging;

/// <summary>
/// Messaging helpers for organization tiers.
/// </summary>
public static class OrganizationTierMessagingExtensions
{
    extension(OrganizationTier tier)
    {
        /// <summary>
        /// Converts an organization tier to the stable route segment used in queue names.
        /// </summary>
        /// <returns>Lowercase routing segment for the tier.</returns>
        public string ToRoutingName() =>
            tier switch
            {
                OrganizationTier.Priority => "priority",
                OrganizationTier.Default => "default",
                OrganizationTier.Low => "low",
                _ => throw new ArgumentOutOfRangeException(
                    nameof(tier),
                    tier,
                    "Unknown organization tier."
                ),
            };
    }

    extension(TenantBucketRoutingOptions options)
    {
        /// <summary>
        /// Gets the configured bucket count for a tier.
        /// </summary>
        /// <param name="tier">Organization tier.</param>
        /// <returns>Configured bucket count.</returns>
        public int GetBucketCount(OrganizationTier tier) =>
            tier switch
            {
                OrganizationTier.Priority => options.PriorityBucketCount,
                OrganizationTier.Default => options.DefaultBucketCount,
                OrganizationTier.Low => options.LowBucketCount,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(tier),
                    tier,
                    "Unknown organization tier."
                ),
            };
    }
}
