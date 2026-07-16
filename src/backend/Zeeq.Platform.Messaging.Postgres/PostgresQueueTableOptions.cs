using Zeeq.Core.Models;

namespace Zeeq.Platform.Messaging.Postgres;

/// <summary>
/// Queue table names used by the Brighter Postgres transport.
/// </summary>
public sealed class PostgresQueueTableOptions
{
    /// <summary>
    /// Queue table for priority-tier organizations.
    /// </summary>
    public string Priority { get; init; } = "brighter_messages_priority";

    /// <summary>
    /// Queue table for default-tier organizations.
    /// </summary>
    public string Default { get; init; } = "brighter_messages";

    /// <summary>
    /// Queue table for low-tier organizations.
    /// </summary>
    public string Low { get; init; } = "brighter_messages_low";

    /// <summary>
    /// Queue table for non-tenant system work.
    /// </summary>
    public string System { get; init; } = "brighter_messages_system";

    /// <summary>
    /// Queue table for immediate user-visible acknowledgement work.
    /// </summary>
    public string Immediate { get; init; } = "brighter_messages_immediate";

    /// <summary>
    /// Gets the queue table for an organization tier.
    /// </summary>
    /// <param name="tier">Organization tier.</param>
    /// <returns>Queue table name.</returns>
    public string GetTenantTable(OrganizationTier tier) =>
        tier switch
        {
            OrganizationTier.Priority => Priority,
            OrganizationTier.Default => Default,
            OrganizationTier.Low => Low,
            _ => throw new ArgumentOutOfRangeException(
                nameof(tier),
                tier,
                "Unknown organization tier."
            ),
        };
}
