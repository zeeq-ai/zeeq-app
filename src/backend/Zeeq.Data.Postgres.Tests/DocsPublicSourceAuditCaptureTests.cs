using Microsoft.EntityFrameworkCore;
using Zeeq.Core.Auditing;
using Zeeq.Core.Documents;
using Zeeq.Data.Postgres.Auditing;
using Zeeq.Data.Postgres.Documents;
using Zeeq.Testing;

namespace Zeeq.Data.Postgres.Tests;

/// <summary>
/// Guards global source registration capture, independent subscription commits, and failed-save cleanup.
/// Run: dotnet run --project src/backend/Zeeq.Data.Postgres.Tests -- --treenode-filter "/*/*/DocsPublicSourceAuditCaptureTests/*"
/// </summary>
[Property("integration", "true")]
[Property("testcontainer", "true")]
[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class DocsPublicSourceAuditCaptureTests(PgDatabaseFixture postgres)
    : PgTransactionalTestBase(postgres)
{
    private static DocsPublicSource Source(string suffix, string? repoUrl = null) =>
        new()
        {
            Id = $"source_audit_{suffix}",
            Kind = RepositorySourceKind.Public,
            RepoUrl = repoUrl ?? $"https://github.com/audit/{suffix}",
            Name = $"Source {suffix}",
            DefaultIncludeFilters = ["docs/**"],
            DefaultExcludeFilters = ["private/**"],
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    [Test]
    public async Task Create_RecordsGlobalFactWithTrustedActor_ReuseDoesNotAppend()
    {
        var actors = new AuditActorContext
        {
            Current = new AuditActor(
                AuditActorKinds.User,
                "source_creator",
                "Source creator",
                AuditChannels.Web
            ),
        };
        var writer = new PostgresAuditTrailWriter(_context, actors);
        var store = new PostgresDocsPublicSourceStore(_context, writer);
        var source = Source(
            "created",
            "https://user:credential@github.com/audit/created?token=private"
        );

        await store.CreateAsync(source, default);
        var reused = await store.GetByRepoUrlAsync(source.RepoUrl, default);
        var row = await _context.SystemAuditEvents.AsNoTracking().SingleAsync();
        var detail = await new PostgresAuditTrailReader(_context).GetSystemEventAsync(
            row.Id,
            row.OccurredAtUtc,
            default
        );

        await Assert.That(reused!.Id).IsEqualTo(source.Id);
        await Assert.That(row.EventType).IsEqualTo("public_source.created");
        await Assert.That(row.SubjectId).IsEqualTo(source.Id);
        await Assert.That(row.ActorId).IsEqualTo("source_creator");
        await Assert.That(row.OperationId).IsEqualTo(actors.OperationId);
        await Assert.That(detail!.Event.Scope).IsEqualTo(AuditScope.System);
        await Assert.That(detail.Before).IsNull();
        await Assert.That(detail.After!.Fields["includeFilters"]).IsEqualTo("[\"docs/**\"]");
        await Assert.That(row.AfterSnapshot!.Contains("credential")).IsFalse();
        await Assert
            .That(row.AfterSnapshot.Contains("github.com", StringComparison.Ordinal))
            .IsFalse();
        await Assert.That(await _context.OrganizationAuditEvents.CountAsync()).IsEqualTo(0);
    }

    [Test]
    public async Task Create_AuditFailureCannotLeaveSourceOrFactForLaterCallerSave()
    {
        var writer = new PostgresAuditTrailWriter(_context, new AuditActorContext());
        var store = new PostgresDocsPublicSourceStore(_context, writer);
        var existing = Source("existing");
        await store.CreateAsync(existing, default);
        var existingFact = _context.SystemAuditEvents.Local.Single();
        var failing = new PostgresDocsPublicSourceStore(_context, new AttachThenThrow(writer));
        var rejected = Source("rejected");

        await Assert
            .That(async () =>
            {
                await failing.CreateAsync(rejected, default);
            })
            .Throws<InvalidOperationException>();
        await _context.SaveChangesAsync();

        await Assert.That(_context.Entry(existingFact).State).IsEqualTo(EntityState.Unchanged);
        await Assert.That(await store.GetByIdAsync(rejected.Id, default)).IsNull();
        await Assert.That(await _context.SystemAuditEvents.CountAsync()).IsEqualTo(1);
        await Assert.That(await _context.DocsPublicSources.CountAsync()).IsEqualTo(1);
    }

    [Test]
    public async Task Create_UniqueConflictDoesNotLeakFactIntoSubsequentSuccessfulSave()
    {
        var store = new PostgresDocsPublicSourceStore(_context);
        var first = Source("first");
        await store.CreateAsync(first, default);
        var duplicate = Source("duplicate", first.RepoUrl);

        await Assert
            .That(async () =>
            {
                await store.CreateAsync(duplicate, default);
            })
            .Throws<DbUpdateException>();
        await store.CreateAsync(Source("next"), default);

        await Assert.That(await store.GetByIdAsync(duplicate.Id, default)).IsNull();
        await Assert.That(await _context.SystemAuditEvents.CountAsync()).IsEqualTo(2);
        await Assert.That(await _context.DocsPublicSources.CountAsync()).IsEqualTo(2);
    }

    [Test]
    public async Task Create_CallerRollbackRemovesRegistrationAndItsFactTogether()
    {
        var transaction = _context.Database.CurrentTransaction!;
        await transaction.CreateSavepointAsync("source_capture");
        var source = Source("rollback");
        await new PostgresDocsPublicSourceStore(_context).CreateAsync(source, default);

        await transaction.RollbackToSavepointAsync("source_capture");
        _context.ChangeTracker.Clear();

        await Assert.That(await _context.DocsPublicSources.CountAsync()).IsEqualTo(0);
        await Assert.That(await _context.SystemAuditEvents.CountAsync()).IsEqualTo(0);
    }

    private sealed class AttachThenThrow(IAuditTrailWriter inner) : IAuditTrailWriter
    {
        public void AppendOrganization(string organizationId, string? teamId, AuditChange change) =>
            inner.AppendOrganization(organizationId, teamId, change);

        public void AppendSystem(AuditChange change)
        {
            inner.AppendSystem(change);
            throw new InvalidOperationException("Injected source capture failure.");
        }
    }
}
