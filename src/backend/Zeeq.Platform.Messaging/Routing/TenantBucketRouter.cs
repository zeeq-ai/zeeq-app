using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Zeeq.Core.Models;

namespace Zeeq.Platform.Messaging;

/// <summary>
/// Computes stable tenant bucket routes for organization-scoped messages.
/// </summary>
/// <remarks>
/// This router intentionally does not use <see cref="object.GetHashCode"/>
/// because runtime hash behavior is not a durable routing contract. SHA-256 is
/// slower than non-cryptographic hashes, but routing happens once per publish
/// and the stable cross-process behavior is more important here.
/// </remarks>
public sealed class TenantBucketRouter
{
    /// <summary>
    /// Computes the deterministic bucket index for an organization.
    /// </summary>
    /// <param name="organizationId">Organization identifier used as the distribution key.</param>
    /// <param name="bucketCount">Number of buckets in the target tier.</param>
    /// <returns>Bucket index in the range <c>0..bucketCount - 1</c>.</returns>
    public int ToBucket(string organizationId, int bucketCount)
    {
        if (string.IsNullOrWhiteSpace(organizationId))
        {
            throw new ArgumentException("Organization id is required.", nameof(organizationId));
        }

        if (bucketCount < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bucketCount),
                bucketCount,
                "Bucket count must be greater than zero."
            );
        }

        var canonicalId = organizationId.Trim().ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalId));
        var value = BinaryPrimitives.ReadUInt64BigEndian(hash.AsSpan(0, sizeof(ulong)));

        return (int)(value % (ulong)bucketCount);
    }

    /// <summary>
    /// Builds the concrete tier-and-bucket route for a tenant message.
    /// </summary>
    /// <param name="topic">Logical base topic from the publisher declaration.</param>
    /// <param name="organizationId">Organization identifier used as the distribution key.</param>
    /// <param name="tier">Resolved organization tier.</param>
    /// <param name="options">Tenant bucket routing options.</param>
    /// <returns>Concrete route metadata.</returns>
    public TenantBucketRoute ToRoute(
        string topic,
        string organizationId,
        OrganizationTier tier,
        TenantBucketRoutingOptions options
    )
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            throw new ArgumentException("Topic is required.", nameof(topic));
        }

        var bucketCount = options.GetBucketCount(tier);
        var bucket = ToBucket(organizationId, bucketCount);
        var routingKey = new TenantRoutingKey(topic, tier, bucket);

        return new TenantBucketRoute(routingKey, tier, bucket, bucketCount);
    }
}
