using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Zeeq.Core.Auditing;
using Zeeq.Data.Postgres.Auditing;
using Zeeq.Testing;

namespace Zeeq.Data.Postgres.Tests;

/// <summary>
/// Foundation guards for the audit trail: atomic append, tenant/team/library isolation, keyset
/// paging (including timestamp ties), metadata-only feeds, and the partitioned schema.
///
/// Run:
/// dotnet run --project src/backend/Zeeq.Data.Postgres.Tests --output detailed --disable-logo --treenode-filter "/*/*/AuditTrailFoundationTests/*"
/// </summary>
[Property("integration", "true")]
[Property("testcontainer", "true")]
[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class AuditTrailFoundationTests(PgDatabaseFixture postgres)
    : PgTransactionalTestBase(postgres)
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    private PostgresAuditTrailWriter Writer(AuditActorContext? actors = null) =>
        new(_context, actors ?? new AuditActorContext());

    private PostgresAuditTrailReader Reader() => new(_context);

    private static AuditChange Change(
        string label = "docs/a.md",
        string? libraryId = "lib_1",
        AuditSnapshot? after = null
    ) =>
        new()
        {
            EventType = "document.content_updated",
            ChangeType = AuditChangeType.Edited,
            Area = AuditArea.Content,
            SubjectType = "document",
            SubjectId = "doc_1",
            SubjectLabel = label,
            LibraryId = libraryId,
            After = after,
        };

    private static OrganizationAuditEvent OrgRow(
        string org,
        DateTimeOffset at,
        Guid? id = null,
        string? team = null,
        string? library = "lib_1",
        bool surface = false
    ) =>
        new()
        {
            Id = id ?? Guid.CreateVersion7(),
            OccurredAtUtc = at,
            OrganizationId = org,
            TeamId = team,
            SurfaceInSystem = surface,
            EventType = "document.content_updated",
            ChangeType = nameof(AuditChangeType.Edited),
            Area = nameof(AuditArea.Content),
            SubjectType = "document",
            SubjectId = "doc_1",
            SubjectLabel = "docs/a.md",
            LibraryId = library,
            ActorKind = AuditActorKinds.User,
            ActorId = "user_1",
            ActorDisplayName = "Alice",
            ActorChannel = AuditChannels.Web,
            OperationId = Guid.NewGuid(),
        };

    private static SystemAuditEvent SystemRow(DateTimeOffset at, Guid? id = null) =>
        new()
        {
            Id = id ?? Guid.CreateVersion7(),
            OccurredAtUtc = at,
            EventType = "activation_key.created",
            ChangeType = nameof(AuditChangeType.Added),
            Area = nameof(AuditArea.Security),
            SubjectType = "activation_key",
            SubjectId = "key_1",
            SubjectLabel = "key_1",
            ActorKind = AuditActorKinds.SystemAdmin,
            ActorId = "admin_1",
            ActorDisplayName = "Root",
            ActorChannel = AuditChannels.SystemConsole,
            OperationId = Guid.NewGuid(),
        };

    [Test]
    public async Task Writer_Append_OnlyAttachesAndNeverCommitsOnItsOwn()
    {
        // The writer must leave persistence to the caller's unit of work.
        Writer().AppendOrganization("org_w1", null, Change());
        await Assert
            .That(_context.ChangeTracker.Entries<OrganizationAuditEvent>().Count())
            .IsEqualTo(1);

        _context.ChangeTracker.Clear();

        await Assert
            .That(
                await _context.OrganizationAuditEvents.CountAsync(e => e.OrganizationId == "org_w1")
            )
            .IsEqualTo(0);
    }

    [Test]
    public async Task Writer_Fact_RollsBackWithCallerTransaction()
    {
        var transaction = _context.Database.CurrentTransaction!;
        await transaction.CreateSavepointAsync("before_fact");
        Writer().AppendOrganization("org_w2", null, Change());
        await _context.SaveChangesAsync();
        await Assert
            .That(
                await _context.OrganizationAuditEvents.CountAsync(e => e.OrganizationId == "org_w2")
            )
            .IsEqualTo(1);

        await transaction.RollbackToSavepointAsync("before_fact");
        _context.ChangeTracker.Clear();

        await Assert
            .That(
                await _context.OrganizationAuditEvents.CountAsync(e => e.OrganizationId == "org_w2")
            )
            .IsEqualTo(0);
    }

    [Test]
    [Arguments("apiKey")]
    [Arguments("Password")]
    [Arguments("clientSecret")]
    [Arguments("refreshToken")]
    [Arguments("KeyHash")]
    [Arguments("api_key")]
    [Arguments("key_hash")]
    [Arguments("private-key")]
    [Arguments("ciphertext")]
    public async Task Writer_SecretLikeSnapshotField_IsRejected(string field)
    {
        var snapshot = new AuditSnapshot
        {
            Fields = new Dictionary<string, string?> { [field] = "x" },
        };

        await Assert
            .That(() => Writer().AppendOrganization("org_w3", null, Change(after: snapshot)))
            .Throws<ArgumentException>();
        await Assert.That(_context.ChangeTracker.Entries<OrganizationAuditEvent>().Any()).IsFalse();
    }

    [Test]
    [Arguments("TokenCount")]
    [Arguments("UserTokenId")]
    [Arguments("Role")]
    public async Task Writer_FactFieldNames_AreAllowed(string field)
    {
        var snapshot = new AuditSnapshot
        {
            Fields = new Dictionary<string, string?> { [field] = "1" },
        };

        Writer().AppendOrganization("org_w4", null, Change(after: snapshot));

        await Assert
            .That(_context.ChangeTracker.Entries<OrganizationAuditEvent>().Count())
            .IsEqualTo(1);
    }

    [Test]
    public async Task Writer_ActorModes_AreStoredWithoutConflatingOwnerAndCredential()
    {
        var credential = new AuditActor(
            Kind: AuditActorKinds.ClientCredential,
            Id: "client_1",
            DisplayName: "CI key",
            Channel: AuditChannels.Api,
            ResponsibleUserId: "human_1",
            CredentialId: "client_1",
            CredentialName: "CI key"
        );
        var writer = Writer(new AuditActorContext { Current = credential });

        writer.AppendOrganization("org_a1", null, Change());
        writer.AppendOrganization(
            "org_a1",
            null,
            Change() with
            {
                ActorOverride = AuditActor.Worker("repository-sync"),
            }
        );
        await _context.SaveChangesAsync();

        var rows = await _context
            .OrganizationAuditEvents.AsNoTracking()
            .Where(e => e.OrganizationId == "org_a1")
            .ToListAsync();
        var byCredential = rows.Single(e => e.ActorKind == AuditActorKinds.ClientCredential);
        await Assert.That(byCredential.ActorId).IsEqualTo("client_1");
        await Assert.That(byCredential.ActorResponsibleUserId).IsEqualTo("human_1");
        await Assert.That(byCredential.SurfaceInSystem).IsFalse();
        var byWorker = rows.Single(e => e.ActorKind == AuditActorKinds.System);
        await Assert.That(byWorker.ActorChannel).IsEqualTo(AuditChannels.Worker);
        await Assert.That(byWorker.ActorResponsibleUserId).IsNull();
    }

    [Test]
    public async Task Writer_OperationIdIsSharedAcrossScopes_AndSystemAdminSurfacesInBothViews()
    {
        var admin = new AuditActor(
            Kind: AuditActorKinds.SystemAdmin,
            Id: "admin_1",
            DisplayName: "Root",
            Channel: AuditChannels.SystemConsole,
            ResponsibleUserId: "admin_1"
        );
        var context = new AuditActorContext { Current = admin };
        var writer = Writer(context);

        writer.AppendOrganization("org_s1", null, Change());
        writer.AppendSystem(Change() with { SubjectType = "organization" });
        await _context.SaveChangesAsync();

        var tenant = await _context
            .OrganizationAuditEvents.AsNoTracking()
            .SingleAsync(e => e.OrganizationId == "org_s1");
        var global = await _context.SystemAuditEvents.AsNoTracking().SingleAsync();
        await Assert.That(tenant.OperationId).IsEqualTo(context.OperationId);
        await Assert.That(global.OperationId).IsEqualTo(context.OperationId);
        await Assert.That(tenant.SurfaceInSystem).IsTrue();

        var system = await Reader().ListSystemAsync(new AuditHistoryQuery(), default);
        await Assert
            .That(system.Entries.Count(e => e.OperationId == context.OperationId))
            .IsEqualTo(2);
        var tenantView = await Reader()
            .ListOrganizationAsync("org_s1", null, new AuditHistoryQuery(), default);
        await Assert.That(tenantView.Entries.Count).IsEqualTo(1);
        await Assert.That(tenantView.Entries[0].Actor.Kind).IsEqualTo(AuditActorKinds.SystemAdmin);
    }

    [Test]
    public async Task OrganizationFeed_IsolatesTenants_AndDetailCannotCrossTenants()
    {
        var a = OrgRow("org_iso_a", T0);
        var b = OrgRow("org_iso_b", T0.AddMinutes(1));
        _context.OrganizationAuditEvents.AddRange(a, b);
        await _context.SaveChangesAsync();

        var page = await Reader().ListOrganizationAsync("org_iso_a", null, new(), default);

        await Assert.That(page.Entries.Select(e => e.Id)).IsEquivalentTo([a.Id]);
        await Assert
            .That(
                await Reader()
                    .GetOrganizationEventAsync("org_iso_b", null, a.Id, a.OccurredAtUtc, default)
            )
            .IsNull();
        await Assert
            .That(
                await Reader()
                    .GetOrganizationEventAsync("org_iso_a", null, a.Id, a.OccurredAtUtc, default)
            )
            .IsNotNull();
    }

    [Test]
    public async Task OrganizationFeed_TeamScopedReader_SeesOwnTeamAndOrgSharedOnly()
    {
        var shared = OrgRow("org_team", T0, team: null);
        var mine = OrgRow("org_team", T0.AddMinutes(1), team: "team_1");
        var other = OrgRow("org_team", T0.AddMinutes(2), team: "team_2");
        _context.OrganizationAuditEvents.AddRange(shared, mine, other);
        await _context.SaveChangesAsync();

        var team = await Reader().ListOrganizationAsync("org_team", "team_1", new(), default);
        var admin = await Reader().ListOrganizationAsync("org_team", null, new(), default);

        await Assert.That(team.Entries.Select(e => e.Id)).IsEquivalentTo([shared.Id, mine.Id]);
        await Assert.That(admin.Entries.Count).IsEqualTo(3);
        await Assert
            .That(
                await Reader()
                    .GetOrganizationEventAsync(
                        "org_team",
                        "team_1",
                        other.Id,
                        other.OccurredAtUtc,
                        default
                    )
            )
            .IsNull();
    }

    [Test]
    public async Task OrganizationFeed_RestrictToLibraries_HidesOtherAndLibraryLessEvents()
    {
        var visible = OrgRow("org_lib", T0, library: "lib_ok");
        var hidden = OrgRow("org_lib", T0.AddMinutes(1), library: "lib_gone");
        var orgWide = OrgRow("org_lib", T0.AddMinutes(2), library: null);
        _context.OrganizationAuditEvents.AddRange(visible, hidden, orgWide);
        await _context.SaveChangesAsync();

        var page = await Reader()
            .ListOrganizationAsync(
                "org_lib",
                null,
                new AuditHistoryQuery { RestrictToLibraryIds = ["lib_ok"] },
                default
            );

        await Assert.That(page.Entries.Select(e => e.Id)).IsEquivalentTo([visible.Id]);
    }

    [Test]
    public async Task SystemFeed_IncludesSystemEventsAndSurfacedTenantEventsOnly()
    {
        var global = SystemRow(T0);
        var surfaced = OrgRow("org_sys", T0.AddMinutes(1), surface: true);
        var tenantOnly = OrgRow("org_sys", T0.AddMinutes(2), surface: false);
        _context.SystemAuditEvents.Add(global);
        _context.OrganizationAuditEvents.AddRange(surfaced, tenantOnly);
        await _context.SaveChangesAsync();

        var page = await Reader()
            .ListSystemAsync(
                new AuditHistoryQuery { FromUtc = T0, ToUtc = T0.AddHours(1) },
                default
            );

        await Assert.That(page.Entries.Select(e => e.Id)).IsEquivalentTo([global.Id, surfaced.Id]);
        await Assert
            .That(page.Entries.Single(e => e.Id == surfaced.Id).Scope)
            .IsEqualTo(AuditScope.Organization);
        await Assert
            .That(page.Entries.Single(e => e.Id == global.Id).Scope)
            .IsEqualTo(AuditScope.System);
        await Assert
            .That(await Reader().GetSystemEventAsync(surfaced.Id, surfaced.OccurredAtUtc, default))
            .IsNotNull();
        await Assert
            .That(
                await Reader().GetSystemEventAsync(tenantOnly.Id, tenantOnly.OccurredAtUtc, default)
            )
            .IsNull();
    }

    [Test]
    public async Task Cursor_PagesThroughTimestampTies_WithoutDuplicatesOrGaps()
    {
        // Identical timestamps force the tie-break on id; every row must appear exactly once.
        var rows = Enumerable
            .Range(0, 7)
            .Select(i => OrgRow("org_tie", i < 5 ? T0 : T0.AddMinutes(-1)))
            .ToList();
        _context.OrganizationAuditEvents.AddRange(rows);
        await _context.SaveChangesAsync();

        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await Reader()
                .ListOrganizationAsync(
                    "org_tie",
                    null,
                    new() { Limit = 2, Cursor = cursor },
                    default
                );
            seen.AddRange(page.Entries.Select(e => e.Id));
            cursor = page.NextCursor;
        } while (cursor is not null);

        await Assert.That(seen.Count).IsEqualTo(7);
        await Assert.That(seen.Distinct().Count()).IsEqualTo(7);
        await Assert.That(seen.ToHashSet()).IsEquivalentTo(rows.Select(r => r.Id).ToHashSet());
    }

    [Test]
    public async Task SystemCursor_MergesTwoTablesInStableOrder_AcrossTies()
    {
        var rows = new List<(Guid Id, DateTimeOffset At)>();
        for (var i = 0; i < 4; i++)
        {
            var system = SystemRow(T0);
            var tenant = OrgRow("org_merge", T0, surface: true);
            _context.SystemAuditEvents.Add(system);
            _context.OrganizationAuditEvents.Add(tenant);
            rows.Add((system.Id, T0));
            rows.Add((tenant.Id, T0));
        }
        await _context.SaveChangesAsync();

        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await Reader()
                .ListSystemAsync(
                    new()
                    {
                        Limit = 3,
                        Cursor = cursor,
                        FromUtc = T0.AddMinutes(-1),
                        ToUtc = T0.AddMinutes(1),
                    },
                    default
                );
            seen.AddRange(page.Entries.Select(e => e.Id));
            cursor = page.NextCursor;
        } while (cursor is not null);

        await Assert.That(seen.Count).IsEqualTo(8);
        await Assert.That(seen.Distinct().Count()).IsEqualTo(8);
        // Postgres compares uuid bytes big-endian; the merge must follow that order exactly.
        var expected = rows.Select(r => r.Id)
            .OrderByDescending(
                id => id.ToByteArray(bigEndian: true),
                Comparer<byte[]>.Create((x, y) => x.AsSpan().SequenceCompareTo(y))
            )
            .ToList();
        await Assert.That(seen).IsEquivalentTo(expected);
    }

    [Test]
    public async Task Filters_NarrowByTypeAreaSubjectLibraryOperationAndRange()
    {
        var target = OrgRow("org_flt", T0, library: "lib_x");
        target.SubjectId = "doc_target";
        var op = Guid.NewGuid();
        target.OperationId = op;
        var other = OrgRow("org_flt", T0.AddDays(-30), library: "lib_y");
        other.ChangeType = nameof(AuditChangeType.Deleted);
        other.Area = nameof(AuditArea.Settings);
        _context.OrganizationAuditEvents.AddRange(target, other);
        await _context.SaveChangesAsync();

        async Task<int> Count(AuditHistoryQuery q) =>
            (await Reader().ListOrganizationAsync("org_flt", null, q, default)).Entries.Count;

        await Assert.That(await Count(new())).IsEqualTo(2);
        await Assert.That(await Count(new() { ChangeType = AuditChangeType.Deleted })).IsEqualTo(1);
        await Assert.That(await Count(new() { Area = AuditArea.Content })).IsEqualTo(1);
        await Assert
            .That(await Count(new() { SubjectType = "document", SubjectId = "doc_target" }))
            .IsEqualTo(1);
        await Assert.That(await Count(new() { LibraryId = "lib_y" })).IsEqualTo(1);
        await Assert.That(await Count(new() { OperationId = op })).IsEqualTo(1);
        await Assert.That(await Count(new() { FromUtc = T0.AddDays(-1) })).IsEqualTo(1);
        await Assert.That(await Count(new() { ToUtc = T0.AddDays(-1) })).IsEqualTo(1);
        await Assert.That(await Count(new() { ActorId = "someone_else" })).IsEqualTo(0);
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    [Arguments(AuditLimits.MaxPageSize + 1)]
    public async Task List_InvalidLimit_IsRejected(int limit)
    {
        await Assert
            .That(async () =>
                await Reader()
                    .ListOrganizationAsync("org_v", null, new() { Limit = limit }, default)
            )
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task List_InvalidCursorOrRange_IsRejected()
    {
        await Assert
            .That(async () =>
                await Reader()
                    .ListOrganizationAsync(
                        "org_v",
                        null,
                        new() { Cursor = "not-a-cursor" },
                        default
                    )
            )
            .Throws<ArgumentException>();
        await Assert
            .That(async () =>
                await Reader()
                    .ListSystemAsync(new() { FromUtc = T0.AddDays(1), ToUtc = T0 }, default)
            )
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Detail_RequiresExactEventTimestamp_AndRoundTripsTypedSnapshots()
    {
        var snapshot = new AuditSnapshot
        {
            Path = "docs/a.md",
            Title = "A",
            Content = "# Hello\n",
            Fields = new Dictionary<string, string?> { ["ExcludedFromCodeReviews"] = "false" },
        };
        var writer = Writer();
        writer.AppendOrganization("org_det", null, Change(after: snapshot));
        await _context.SaveChangesAsync();
        var stored = await _context
            .OrganizationAuditEvents.AsNoTracking()
            .SingleAsync(e => e.OrganizationId == "org_det");

        var detail = await Reader()
            .GetOrganizationEventAsync("org_det", null, stored.Id, stored.OccurredAtUtc, default);
        var wrongTime = await Reader()
            .GetOrganizationEventAsync(
                "org_det",
                null,
                stored.Id,
                stored.OccurredAtUtc.AddSeconds(1),
                default
            );

        await Assert.That(wrongTime).IsNull();
        await Assert.That(detail!.Event.HasSnapshots).IsTrue();
        await Assert.That(detail.Before).IsNull();
        await Assert.That(detail.After!.Content).IsEqualTo("# Hello\n");
        await Assert.That(detail.After.Path).IsEqualTo("docs/a.md");
        await Assert.That(detail.After.Fields["ExcludedFromCodeReviews"]).IsEqualTo("false");
    }

    [Test]
    public async Task Writer_TimestampIsMicrosecondPrecise_SoFeedValueResolvesDetail()
    {
        Writer().AppendOrganization("org_us", null, Change());
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var entry = (
            await Reader().ListOrganizationAsync("org_us", null, new(), default)
        ).Entries.Single();

        await Assert.That(entry.OccurredAtUtc.Ticks % TimeSpan.TicksPerMicrosecond).IsEqualTo(0L);
        await Assert
            .That(
                await Reader()
                    .GetOrganizationEventAsync(
                        "org_us",
                        null,
                        entry.Id,
                        entry.OccurredAtUtc,
                        default
                    )
            )
            .IsNotNull();
    }

    private sealed class SqlCapture : DbCommandInterceptor
    {
        public List<string> Statements { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default
        )
        {
            Statements.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Test]
    public async Task Feeds_NeverSelectSnapshotColumns_OnlyDetailDoes()
    {
        // Snapshots can be large (document content); only the detail query may read their values.
        var big = new AuditSnapshot { Content = new string('x', 200_000) };
        Writer().AppendOrganization("org_sql", null, Change(after: big));
        Writer().AppendSystem(Change(after: big));
        await _context.SaveChangesAsync();
        var stored = await _context
            .OrganizationAuditEvents.AsNoTracking()
            .SingleAsync(e => e.OrganizationId == "org_sql");

        var capture = new SqlCapture();
        await using var probe = new PostgresDbContext(
            new DbContextOptionsBuilder<PostgresDbContext>()
                .UseNpgsql(
                    _context.Database.GetDbConnection(),
                    npgsql =>
                    {
                        npgsql.SetPostgresVersion(18, 0);
                        npgsql.UseVector();
                    }
                )
                .UseOpenIddict()
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(capture)
                .Options
        );
        await probe.Database.UseTransactionAsync(
            _context.Database.CurrentTransaction!.GetDbTransaction()
        );
        var reader = new PostgresAuditTrailReader(probe);

        var page = await reader.ListOrganizationAsync("org_sql", null, new(), default);
        await reader.ListSystemAsync(new(), default);
        var listSql = capture.Statements.ToList();
        capture.Statements.Clear();
        await reader.GetOrganizationEventAsync(
            "org_sql",
            null,
            stored.Id,
            stored.OccurredAtUtc,
            default
        );
        var detailSql = capture.Statements.ToList();

        await Assert.That(page.Entries.Single().HasSnapshots).IsTrue();
        await Assert.That(listSql.Count).IsGreaterThanOrEqualTo(3);
        foreach (var sql in listSql)
        {
            // Null checks do not detoast; any other mention would mean the value is selected.
            var withoutNullChecks = System.Text.RegularExpressions.Regex.Replace(
                sql,
                @"\w+\.(before|after)_snapshot IS NOT NULL",
                ""
            );
            await Assert
                .That(withoutNullChecks.Contains("_snapshot", StringComparison.Ordinal))
                .IsFalse();
        }
        await Assert
            .That(detailSql.Single().Contains("after_snapshot", StringComparison.Ordinal))
            .IsTrue();
    }

    [Test]
    public async Task Tables_ArePartitionedWithoutAutomaticRetention()
    {
        // History must never be dropped or detached automatically.
        var partitioned = await _context
            .Database.SqlQuery<string>(
                $"""
                SELECT c.relname AS "Value"
                FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'zeeq' AND c.relkind = 'p'
                  AND c.relname IN ('organization_audit_events', 'system_audit_events')
                """
            )
            .ToListAsync();
        await Assert.That(partitioned.Count).IsEqualTo(2);

        // pg_partman may be installed in any schema; the migration discovers it the same way.
        var partmanSchema = await _context
            .Database.SqlQuery<string>(
                $"""
                SELECT n.nspname AS "Value"
                FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE c.relname = 'part_config'
                ORDER BY (n.nspname = 'partman') DESC, n.nspname
                LIMIT 1
                """
            )
            .SingleAsync();
        // Identifiers cannot be SQL parameters. Quote the schema discovered from pg_catalog.
        var quotedSchema = new Npgsql.NpgsqlCommandBuilder().QuoteIdentifier(partmanSchema);
        var configSql = $"""
            SELECT concat_ws('|', parent_table, partition_interval, coalesce(retention, 'NULL'), retention_keep_table::text) AS "Value"
            FROM {quotedSchema}.part_config
            WHERE parent_table IN ('zeeq.organization_audit_events', 'zeeq.system_audit_events')
            """;
        var config = await _context.Database.SqlQueryRaw<string>(configSql).ToListAsync();
        await Assert.That(config.Count).IsEqualTo(2);
        foreach (var row in config)
        {
            var parts = row.Split('|');
            await Assert.That(parts[1]).IsEqualTo("1 mon");
            await Assert.That(parts[2]).IsEqualTo("NULL");
        }
    }

    [Test]
    public async Task Partitions_AreMonthlyWithUtcMidnightBoundaries_AndFutureMonthsExist()
    {
        await _context.Database.ExecuteSqlRawAsync("SELECT set_config('timezone', 'UTC', true)");
        var bounds = await _context
            .Database.SqlQuery<string>(
                $"""
                SELECT pg_get_expr(child.relpartbound, child.oid) AS "Value"
                FROM pg_inherits link
                JOIN pg_class child ON child.oid = link.inhrelid
                JOIN pg_class parent ON parent.oid = link.inhparent
                WHERE parent.relname IN ('organization_audit_events', 'system_audit_events')
                  AND pg_get_expr(child.relpartbound, child.oid) <> 'DEFAULT'
                """
            )
            .ToListAsync();

        // Two tables, each with the previous month, the current month, and three premade months.
        await Assert.That(bounds.Count).IsGreaterThanOrEqualTo(10);
        foreach (var bound in bounds)
        {
            await Assert
                .That(
                    System.Text.RegularExpressions.Regex.IsMatch(
                        bound,
                        @"FROM \('\d{4}-\d{2}-01 00:00:00\+00'\) TO \('\d{4}-\d{2}-01 00:00:00\+00'\)"
                    )
                )
                .IsTrue();
        }
    }
}
