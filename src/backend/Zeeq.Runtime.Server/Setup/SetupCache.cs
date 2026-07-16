using Microsoft.Extensions.Caching.Hybrid;

namespace Zeeq.Runtime.Server.Setup;

/// <summary>
/// Registers HybridCache and the configured L2 distributed cache provider.
/// </summary>
internal static class CacheExtensions
{
    private static readonly Serilog.ILogger Log = Serilog.Log.ForContext(typeof(CacheExtensions));

    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers the distributed cache (L2) and HybridCache (L1+L2) based on
        /// the configured <see cref="CacheProvider"/>.
        /// </summary>
        public IServiceCollection AddZeeqCache(AppSettings appSettings)
        {
            var cacheSettings = appSettings.Cache;
            var connectionString = appSettings.Database.EffectiveCacheConnectionString;

            if (string.IsNullOrEmpty(connectionString))
            {
                Log.Here()
                    .Warning(
                        "Cache connection string is empty; distributed cache will not be available"
                    );

                return services;
            }

            // Register the L2 IDistributedCache based on configured provider
            switch (cacheSettings.Provider)
            {
                case CacheProvider.Postgres:
                    RegisterPostgresCache(services, cacheSettings, connectionString);
                    break;

                case CacheProvider.Redis:
                    // Redis provider requires the Microsoft.Extensions.Caching.StackExchangeRedis package.
                    // Uncomment RegisterRedisCache below and add the package reference when switching.
                    Log.Here()
                        .Warning(
                            "Redis cache provider selected but StackExchangeRedis package is not installed"
                        );
                    return services;

                default:
                    Log.Here()
                        .Warning(
                            "Unknown cache provider: {Provider}; distributed cache disabled",
                            cacheSettings.Provider
                        );
                    return services;
            }

            // Register HybridCache (L1 + L2) — same regardless of L2 provider
            services.AddHybridCache(options =>
            {
                options.DefaultEntryOptions = new()
                {
                    Expiration = TimeSpan.TryParse(
                        cacheSettings.DefaultEntryExpiration,
                        out var entryExp
                    )
                        ? entryExp
                        : TimeSpan.FromMinutes(10),
                    LocalCacheExpiration = TimeSpan.TryParse(
                        cacheSettings.LocalCacheExpiration,
                        out var localExp
                    )
                        ? localExp
                        : TimeSpan.FromMinutes(2),
                };

                options.MaximumPayloadBytes = cacheSettings.MaximumPayloadBytes;
                options.MaximumKeyLength = cacheSettings.MaximumKeyLength;
            });

            Log.Here()
                .Information(
                    "✅  HybridCache configured. L2 provider: {Provider}",
                    cacheSettings.Provider
                );

            return services;
        }

        private static void RegisterPostgresCache(
            IServiceCollection svc,
            CacheSettings cacheSettings,
            string connectionString
        )
        {
            svc.AddDistributedPostgresCache(options =>
            {
                options.ConnectionString = connectionString;
                options.SchemaName = cacheSettings.SchemaName;
                options.TableName = cacheSettings.TableName;
                options.CreateIfNotExists = cacheSettings.CreateIfNotExists;
                options.UseWAL = cacheSettings.UseWAL;

                if (
                    TimeSpan.TryParse(
                        cacheSettings.ExpiredItemsDeletionInterval,
                        out var deletionInterval
                    )
                )
                    options.ExpiredItemsDeletionInterval = deletionInterval;

                if (
                    TimeSpan.TryParse(
                        cacheSettings.DefaultSlidingExpiration,
                        out var slidingExpiration
                    )
                )
                    options.DefaultSlidingExpiration = slidingExpiration;
            });

            Log.Here()
                .Information(
                    "⚙️  Postgres cache: {Schema}.{Table} (WAL={UseWAL})",
                    cacheSettings.SchemaName,
                    cacheSettings.TableName,
                    cacheSettings.UseWAL
                );
        }

        // Uncomment and add Microsoft.Extensions.Caching.StackExchangeRedis package when switching to Redis.
        // private static void RegisterRedisCache(
        //     IServiceCollection svc,
        //     string connectionString)
        // {
        //     svc.AddStackExchangeRedisCache(options =>
        //     {
        //         options.Configuration = connectionString;
        //     });
        //
        //     Log.Information("Redis cache configured");
        // }
    }

    extension(IServiceProvider services)
    {
        /// <summary>
        /// Forces eager creation of the cache schema and table by performing a
        /// no-op write through HybridCache. This ensures the table exists before
        /// the application starts serving traffic.
        /// </summary>
        public async Task UseZeeqCacheAsync(AppSettings appSettings)
        {
            var cache = services.GetService<HybridCache>();
            if (cache is null)
            {
                Log.Here().Warning("HybridCache is not registered; skipping cache initialization");

                return;
            }

            try
            {
                await cache.GetOrCreateAsync(
                    "__zeeq_cache_init__",
                    _ => ValueTask.FromResult(Array.Empty<byte>()),
                    cancellationToken: CancellationToken.None
                );

                Log.Here().Information("✅  Cache table initialized successfully");
            }
            catch (Exception ex)
            {
                Log.Here().Error(ex, "❌  Failed to initialize cache table");
            }
        }
    }
}
