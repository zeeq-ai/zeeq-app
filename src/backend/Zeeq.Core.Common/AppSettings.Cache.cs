namespace Zeeq.Core.Common;

/// <summary>
/// Configuration for the cache subsystem.
/// </summary>
public sealed partial record AppSettings
{
    /// <summary>
    /// Cache subsystem configuration.
    /// </summary>
    public CacheSettings Cache { get; init; } = new();
}

/// <summary>
/// The distributed cache backend provider.
/// </summary>
public enum CacheProvider
{
    /// <summary>PostgreSQL (unlogged table). Default.</summary>
    Postgres = 0,

    /// <summary>Redis via StackExchange.Redis.</summary>
    Redis = 1,
}

/// <summary>
/// Configuration for the HybridCache L2 distributed cache backend.
/// </summary>
public record CacheSettings
{
    /// <summary>The distributed cache backend to use.</summary>
    public CacheProvider Provider { get; init; } = CacheProvider.Postgres;

    // ── Postgres-specific ──────────────────────────────────────────

    /// <summary>Postgres schema name for the cache table.</summary>
    public string SchemaName { get; init; } = "cache";

    /// <summary>Postgres cache table name.</summary>
    public string TableName { get; init; } = "hybrid_cache";

    /// <summary>Auto-create the cache table if it doesn't exist.</summary>
    public bool CreateIfNotExists { get; init; } = true;

    /// <summary>Use WAL for the cache table. false = UNLOGGED.</summary>
    public bool UseWAL { get; init; } = false;

    // ── Shared (all providers) ─────────────────────────────────────

    /// <summary>Interval for cleaning up expired items (Postgres only).</summary>
    public string ExpiredItemsDeletionInterval { get; init; } = "00:30:00";

    /// <summary>Default sliding expiration for L2 cache entries.</summary>
    public string DefaultSlidingExpiration { get; init; } = "00:20:00";

    /// <summary>Default absolute expiration for HybridCache entries.</summary>
    public string DefaultEntryExpiration { get; init; } = "00:10:00";

    /// <summary>Local (L1) cache expiration. Should be shorter than L2.</summary>
    public string LocalCacheExpiration { get; init; } = "00:02:00";

    /// <summary>Maximum payload size in bytes (default 1 MB).</summary>
    public int MaximumPayloadBytes { get; init; } = 1048576;

    /// <summary>Maximum cache key length.</summary>
    public int MaximumKeyLength { get; init; } = 1024;
}
