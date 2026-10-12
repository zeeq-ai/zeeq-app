using Microsoft.EntityFrameworkCore;
using Zeeq.Biome;
using Zeeq.Biome.EntityFrameworkCore;
using Zeeq.Core.Models;
using Zeeq.Testing.Biomes;

namespace Zeeq.Testing.Biomes;

/// <summary>Starts test scenarios with the seed and session sharing PostgreSQL clock precision.</summary>
public static class TestBiome
{
    private static readonly BiomeOptions<SeedContext> Options = new BiomeOptions<SeedContext>()
        .SeedEntities(seed => seed.GetSeedEntities())
        .Ids(SeedContext.NewId)
        .TimePrecision(TimeSpan.FromTicks(10));

    /// <summary>Creates the requested number of seed users and an optional database session.</summary>
    public static BiomeBuilder<SeedContext, SeedContext> Start(
        DbContext? db,
        out SeedContext seed,
        int userCount = 1,
        TimeProvider? time = null
    )
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(userCount, 1);
        return Start(
            db,
            out seed,
            null,
            Enumerable.Range(0, userCount).Select(_ => (Action<User>)(_ => { })).ToArray(),
            time
        );
    }

    /// <summary>Creates a seed with organization and per-user customization.</summary>
    public static BiomeBuilder<SeedContext, SeedContext> Start(
        DbContext? db,
        out SeedContext seed,
        Action<Organization>? organizationSetup,
        Action<User>[] userSetupActions,
        TimeProvider? time = null
    )
    {
        var clock = time is null
            ? new ScenarioClock(TimeProvider.System.GetUtcNow())
            : new ScenarioClock(time);
        seed = SeedContext.GenerateAt(clock.GetUtcNow(), organizationSetup, userSetupActions);
        return Start(seed, db, clock);
    }

    /// <summary>Starts a no-database scenario with generated seed rows.</summary>
    public static BiomeBuilder<SeedContext, SeedContext> Start(
        out SeedContext seed,
        int userCount = 1,
        TimeProvider? time = null
    ) => Start(null, out seed, userCount, time);

    /// <summary>Uses an explicit seed without replacing its supplied values.</summary>
    public static BiomeBuilder<SeedContext, SeedContext> Start(
        SeedContext seed,
        DbContext? db = null,
        TimeProvider? time = null
    ) => Zeeq.Biome.Biome.Start(seed, Options, db is null ? null : new EfCoreStore(db), time);

    private sealed class ScenarioClock : TimeProvider
    {
        private readonly TimeProvider? _source;
        private readonly DateTimeOffset _frozen;

        public ScenarioClock(DateTimeOffset now) => _frozen = now.TruncateToPostgresPrecision();

        public ScenarioClock(TimeProvider source) => _source = source;

        public override DateTimeOffset GetUtcNow() =>
            (_source?.GetUtcNow() ?? _frozen).ToUniversalTime().TruncateToPostgresPrecision();
    }
}
