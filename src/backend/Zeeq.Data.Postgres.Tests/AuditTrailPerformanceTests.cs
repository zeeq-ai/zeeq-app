using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Zeeq.Core.Auditing;
using Zeeq.Data.Postgres;
using Zeeq.Data.Postgres.Auditing;
using Zeeq.Testing;

namespace Zeeq.Data.Postgres.Tests;

/// <summary>
/// Explicit isolated first-slice probe, not an SLA or a production capacity benchmark.
/// Run explicitly with TUnit's explicit-test option and a filter for this class. Never set
/// ZEEQ_TEST_POSTGRES_CONNECTION_STRING: the probe owns a fresh test container.
/// </summary>
[Explicit]
[NotInParallel]
public sealed class AuditTrailPerformanceTests
{
    [Test]
    public async Task MeasurePartitionedHistory()
    {
        if (
            !string.IsNullOrEmpty(
                Environment.GetEnvironmentVariable("ZEEQ_TEST_POSTGRES_CONNECTION_STRING")
            )
        )
            throw new InvalidOperationException(
                "This probe requires its own test container; unset ZEEQ_TEST_POSTGRES_CONNECTION_STRING."
            );

        await using var postgres = new PgDatabaseFixture();
        await postgres.InitializeAsync();
        await using var db = postgres.CreateContext();
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        var now = DateTimeOffset.UtcNow;
        var month = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        // pg_partman creates last month and the current month at migration time. Create older
        // test-only children before inserting any rows into the default partition.
        for (var i = 2; i < 60; i++)
        {
            var start = month.AddMonths(-i);
            var end = start.AddMonths(1);
            foreach (var table in new[] { "organization_audit_events", "system_audit_events" })
            {
                var child = $"probe_{table}_{start:yyyyMM}";
                var sql =
                    $"CREATE TABLE zeeq.{child} PARTITION OF zeeq.{table} FOR VALUES FROM ('{start:yyyy-MM-dd HH:mm:ss}+00') TO ('{end:yyyy-MM-dd HH:mm:ss}+00')";
                await using var command = new NpgsqlCommand(sql, connection);
                await command.ExecuteNonQueryAsync();
            }
        }

        const string org = "probe-org";
        const int perMonth = 200;
        const int months = 60;
        var clock = new ProbeClock();
        var writer = new PostgresAuditTrailWriter(db, new AuditActorContext(), clock);
        // Typical payloads compress well; one in ten has deterministic, poorly compressible
        // content. Generate once to keep client CPU out of the measured query timings.
        var random = new Random(1729);
        var bytes = new byte[6000];
        random.NextBytes(bytes);
        var noisy = Convert.ToBase64String(bytes);
        var compressible = string.Concat(
            Enumerable.Repeat("Typical document content and headings. ", 211)
        );
        var beforeWal = await ScalarAsync<string>(connection, "SELECT pg_current_wal_lsn()::text");
        var seedTimer = Stopwatch.StartNew();
        for (var i = 0; i < months; i++)
        {
            for (var n = 0; n < perMonth; n++)
            {
                clock.Now = month.AddMonths(-i).AddDays(1).AddSeconds(n);
                var snapshot = new AuditSnapshot
                {
                    Path = $"/docs/{n % 100}.md",
                    Content = n % 10 == 0 ? noisy : compressible,
                };
                var change = new AuditChange
                {
                    EventType = "document.updated",
                    ChangeType = AuditChangeType.Edited,
                    Area = AuditArea.Content,
                    SubjectType = "document",
                    SubjectId = $"doc-{n % 100}",
                    SubjectLabel = $"/docs/{n % 100}.md",
                    LibraryId = "probe-library",
                    Before = snapshot,
                    After = snapshot,
                    ChangedFields = ["content"],
                };
                writer.AppendOrganization(org, teamId: null, change);
                if (n % 20 == 0)
                    writer.AppendSystem(change);
            }
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }
        seedTimer.Stop();
        var afterWal = await ScalarAsync<string>(connection, "SELECT pg_current_wal_lsn()::text");
        await using (
            var analyze = new NpgsqlCommand(
                "ANALYZE zeeq.organization_audit_events; ANALYZE zeeq.system_audit_events",
                connection
            )
        )
            await analyze.ExecuteNonQueryAsync();

        var reader = new PostgresAuditTrailReader(db);
        var first = await reader.ListOrganizationAsync(
            org,
            null,
            new AuditHistoryQuery { Limit = 50 },
            default
        );
        var cursor = first.NextCursor;
        for (var i = 0; i < 20 && cursor is not null; i++)
            cursor = (
                await reader.ListOrganizationAsync(
                    org,
                    null,
                    new AuditHistoryQuery { Limit = 50, Cursor = cursor },
                    default
                )
            ).NextCursor;
        if (cursor is null)
            throw new InvalidOperationException("Not enough events for the deep cursor probe.");
        var measurements = new Dictionary<string, object>();
        foreach (var history in new[] { 12, 36, 60 })
        {
            var from = month.AddMonths(-(history - 1));
            measurements[$"all_time_{history}_months"] = await MeasureAsync(() =>
                reader.ListOrganizationAsync(
                    org,
                    null,
                    new AuditHistoryQuery { FromUtc = from, Limit = 50 },
                    default
                )
            );
        }
        measurements["recent_90_days"] = await MeasureAsync(() =>
            reader.ListOrganizationAsync(
                org,
                null,
                new AuditHistoryQuery { FromUtc = now.AddDays(-90), Limit = 50 },
                default
            )
        );
        measurements["all_time_unbounded"] = await MeasureAsync(() =>
            reader.ListOrganizationAsync(org, null, new AuditHistoryQuery { Limit = 50 }, default)
        );
        measurements["subject"] = await MeasureAsync(() =>
            reader.ListOrganizationAsync(
                org,
                null,
                new AuditHistoryQuery
                {
                    SubjectType = "document",
                    SubjectId = "doc-42",
                    Limit = 50,
                },
                default
            )
        );
        measurements["deep_cursor"] = await MeasureAsync(() =>
            reader.ListOrganizationAsync(
                org,
                null,
                new AuditHistoryQuery { Cursor = cursor, Limit = 50 },
                default
            )
        );
        measurements["system_feed"] = await MeasureAsync(() =>
            reader.ListSystemAsync(new AuditHistoryQuery { Limit = 50 }, default)
        );

        var storage = await ScalarAsync<long>(
            connection,
            "SELECT COALESCE(sum(pg_total_relation_size(inhrelid)),0)::bigint FROM pg_inherits WHERE inhparent IN ('zeeq.organization_audit_events'::regclass, 'zeeq.system_audit_events'::regclass)"
        );
        var toast = await ScalarAsync<long>(
            connection,
            "SELECT COALESCE(sum(pg_total_relation_size(c.reltoastrelid)),0)::bigint FROM pg_inherits i JOIN pg_class c ON c.oid = i.inhrelid WHERE i.inhparent IN ('zeeq.organization_audit_events'::regclass, 'zeeq.system_audit_events'::regclass) AND c.reltoastrelid <> 0"
        );
        var heap = await ScalarAsync<long>(
            connection,
            "SELECT COALESCE(sum(pg_relation_size(inhrelid)),0)::bigint FROM pg_inherits WHERE inhparent IN ('zeeq.organization_audit_events'::regclass, 'zeeq.system_audit_events'::regclass)"
        );
        var indexes = await ScalarAsync<long>(
            connection,
            "SELECT COALESCE(sum(pg_indexes_size(inhrelid)),0)::bigint FROM pg_inherits WHERE inhparent IN ('zeeq.organization_audit_events'::regclass, 'zeeq.system_audit_events'::regclass)"
        );
        var wal = await ScalarAsync<decimal>(
            connection,
            $"SELECT pg_wal_lsn_diff('{afterWal}'::pg_lsn, '{beforeWal}'::pg_lsn)"
        );
        var version = await ScalarAsync<string>(connection, "SELECT version()");
        // Use a separate context so command interception does not affect timing samples. Replay
        // exactly the SQL and typed parameter values sent by the production reader.
        var capture = new ReaderCommandCapture();
        var options = new DbContextOptionsBuilder<PostgresDbContext>()
            .UseNpgsql(
                postgres.ConnectionString,
                npgsql =>
                {
                    npgsql.SetPostgresVersion(18, 0);
                    npgsql.UseVector();
                }
            )
            .UseOpenIddict()
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(capture)
            .Options;
        await using var captureDb = new PostgresDbContext(options);
        var captureReader = new PostgresAuditTrailReader(captureDb);
        var plans = new Dictionary<string, object>();
        plans["recent"] = await CapturePlansAsync(
            capture,
            connection,
            () =>
                captureReader.ListOrganizationAsync(
                    org,
                    null,
                    new AuditHistoryQuery { FromUtc = now.AddDays(-90), Limit = 50 },
                    default
                )
        );
        plans["all_time"] = await CapturePlansAsync(
            capture,
            connection,
            () =>
                captureReader.ListOrganizationAsync(
                    org,
                    null,
                    new AuditHistoryQuery { Limit = 50 },
                    default
                )
        );
        plans["subject"] = await CapturePlansAsync(
            capture,
            connection,
            () =>
                captureReader.ListOrganizationAsync(
                    org,
                    null,
                    new AuditHistoryQuery
                    {
                        SubjectType = "document",
                        SubjectId = "doc-42",
                        Limit = 50,
                    },
                    default
                )
        );
        plans["system_feed_branches"] = await CapturePlansAsync(
            capture,
            connection,
            () => captureReader.ListSystemAsync(new AuditHistoryQuery { Limit = 50 }, default)
        );
        Console.WriteLine(
            JsonSerializer.Serialize(
                new
                {
                    environment = new
                    {
                        postgres = version,
                        runtime = Environment.Version.ToString(),
                        processors = Environment.ProcessorCount,
                        isolatedContainer = true,
                    },
                    scale = new
                    {
                        months,
                        organizationEvents = months * perMonth,
                        systemEvents = months * perMonth / 20,
                        snapshotsPerEvent = 2,
                        snapshotCharacters = noisy.Length,
                        noisyFraction = 0.1,
                        seedMilliseconds = seedTimer.Elapsed.TotalMilliseconds,
                    },
                    measurements,
                    timingCaveat = "30 warm-cache sequential samples per query; p99 is the maximum and is not a production tail-latency estimate.",
                    storageBytesIncludingIndexesAndToast = storage,
                    heapBytes = heap,
                    indexBytesExcludingToast = indexes,
                    toastBytesIncludingIndexes = toast,
                    walBytesPerInsertedEvent = wal / (months * perMonth + months * perMonth / 20),
                    walCaveat = "Cluster-wide WAL delta during seeding; includes indexes and background activity.",
                    planCaveat = "Actual EF reader SQL replayed with typed parameters; plans measure each database branch separately, not the client-side system-feed merge.",
                    plans,
                },
                new JsonSerializerOptions { WriteIndented = true }
            )
        );
    }

    private static async Task<object> CapturePlansAsync(
        ReaderCommandCapture capture,
        NpgsqlConnection connection,
        Func<Task<AuditHistoryPage>> query
    )
    {
        capture.Commands.Clear();
        await query();
        var result = new List<object>();
        foreach (var captured in capture.Commands)
        {
            await using var command = new NpgsqlCommand(
                "EXPLAIN (ANALYZE, BUFFERS, FORMAT TEXT)\n" + captured.Sql,
                connection
            );
            foreach (var parameter in captured.Parameters)
                command.Parameters.Add((NpgsqlParameter)((ICloneable)parameter).Clone());
            var plan = new List<string>();
            await using (var rows = await command.ExecuteReaderAsync())
                while (await rows.ReadAsync())
                    plan.Add(rows.GetString(0));
            result.Add(
                new
                {
                    sql = captured.Sql,
                    parameters = captured
                        .Parameters.Select(p => new
                        {
                            p.ParameterName,
                            type = p.NpgsqlDbType.ToString(),
                            value = p.Value,
                        })
                        .ToArray(),
                    plan,
                }
            );
        }
        if (result.Count == 0)
            throw new InvalidOperationException("Reader did not issue a captured query.");
        return result;
    }

    private sealed class ReaderCommandCapture : DbCommandInterceptor
    {
        public List<CapturedCommand> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default
        )
        {
            Commands.Add(
                new(
                    command.CommandText,
                    command
                        .Parameters.Cast<NpgsqlParameter>()
                        .Select(parameter => (NpgsqlParameter)((ICloneable)parameter).Clone())
                        .ToArray()
                )
            );
            return ValueTask.FromResult(result);
        }
    }

    private sealed record CapturedCommand(string Sql, NpgsqlParameter[] Parameters);

    private static async Task<object> MeasureAsync(Func<Task<AuditHistoryPage>> query)
    {
        for (var i = 0; i < 5; i++)
            await query();
        var samples = new double[30];
        for (var i = 0; i < samples.Length; i++)
        {
            var timer = Stopwatch.StartNew();
            var page = await query();
            timer.Stop();
            if (page.Entries.Count == 0)
                throw new InvalidOperationException("Probe returned no rows.");
            samples[i] = timer.Elapsed.TotalMilliseconds;
        }
        Array.Sort(samples);
        return new
        {
            unit = "milliseconds",
            count = samples.Length,
            p50 = Percentile(samples, .50),
            p95 = Percentile(samples, .95),
            p99 = Percentile(samples, .99),
        };
    }

    private static double Percentile(double[] samples, double p) =>
        samples[(int)Math.Ceiling(samples.Length * p) - 1];

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private sealed class ProbeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
