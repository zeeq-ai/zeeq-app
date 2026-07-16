using Zeeq.Core.Models;

namespace Zeeq.Platform.Messaging;

/// <summary>
/// Structured tenant routing key used for concrete tier-and-bucket routes.
/// </summary>
/// <remarks>
/// The string form is the Brighter routing key, but the route is modeled as a
/// value object so callers can inspect or deconstruct the feature topic,
/// organization tier, and bucket without reparsing a dotted string.
/// </remarks>
/// <param name="Topic">Feature-owned logical topic.</param>
/// <param name="Tier">Organization tier segment.</param>
/// <param name="Bucket">Stable tenant bucket index within the tier.</param>
public sealed record TenantRoutingKey(string Topic, OrganizationTier Tier, int Bucket)
{
    /// <summary>
    /// Feature-owned logical topic.
    /// </summary>
    public string Topic { get; init; } =
        string.IsNullOrWhiteSpace(Topic)
            ? throw new ArgumentException("Topic is required.", nameof(Topic))
            : Topic.Trim();

    /// <summary>
    /// Stable tenant bucket index within the tier.
    /// </summary>
    public int Bucket { get; init; } =
        Bucket < 0
            ? throw new ArgumentOutOfRangeException(
                nameof(Bucket),
                Bucket,
                "Bucket must be zero or greater."
            )
            : Bucket;

    /// <summary>
    /// Converts the structured key to the Brighter routing key string.
    /// </summary>
    /// <returns>Routing key in <c>{topic}.{tier}.{bucket}</c> form.</returns>
    public override string ToString() => $"{Topic}.{Tier.ToRoutingName()}.{Bucket:00}";

    /// <summary>
    /// Converts the structured key to the Brighter routing key string.
    /// </summary>
    /// <param name="routingKey">Structured tenant routing key.</param>
    public static implicit operator string(TenantRoutingKey routingKey) => routingKey.ToString();
}
