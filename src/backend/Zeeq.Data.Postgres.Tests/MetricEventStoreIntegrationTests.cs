using Zeeq.Core.Models;
using Zeeq.Data.Postgres.Metrics;
using Zeeq.Testing;
using Microsoft.EntityFrameworkCore;

namespace Zeeq.Data.Postgres.Tests;

/// <summary>
/// Integration tests for <see cref="PostgresMetricEventStore" /> — the batch append path that the
/// metrics consumer drives. Verifies cross-organization batches persist and scope correctly, and
/// that an empty batch is a no-op.
///
/// Run:
/// dotnet run --project src/backend/Zeeq.Data.Postgres.Tests --output detailed --disable-logo --treenode-filter "/*/*/MetricEventStoreIntegrationTests/*"
/// </summary>
[Property("integration", "true")]
[Property("testcontainer", "true")]
[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class MetricEventStoreIntegrationTests(PgDatabaseFixture postgres)
    : PgTransactionalTestBase(postgres)
{
    [Test]
    public async Task AppendAsync_MixedOrganizationBatch_PersistsAllScopedCorrectly()
    {
        // Guards the consumer's real shape: one batch can carry samples from many organizations
        // (ISystemMessage flat batch), and each must land scoped to its own org with no bleed.
        var store = new PostgresMetricEventStore(_context);
        var now = DateTimeOffset.UtcNow;
        var samples = new List<MetricSample>
        {
            new(
                "org_metrics_store_x",
                null,
                "zeeq_tool_call_counter",
                1,
                "a@x.com",
                "search_documents",
                null,
                null,
                null,
                new() { ["user_agent"] = "cli" },
                now
            ),
            new(
                "org_metrics_store_x",
                null,
                "zeeq_tool_call_counter",
                1,
                "b@x.com",
                "list_documents",
                null,
                null,
                null,
                null,
                now
            ),
            new(
                "org_metrics_store_y",
                "team_y",
                "zeeq_document_read_counter",
                1,
                "c@y.com",
                "read_document_by_path",
                null,
                "zeeq-app",
                null,
                new() { ["path"] = "/guide.md" },
                now
            ),
        };

        await store.AppendAsync(samples, CancellationToken.None);
        _context.ChangeTracker.Clear();

        var xCount = await _context.MetricEvents.CountAsync(e =>
            e.OrganizationId == "org_metrics_store_x"
        );
        var yCount = await _context.MetricEvents.CountAsync(e =>
            e.OrganizationId == "org_metrics_store_y"
        );
        await Assert.That(xCount).IsEqualTo(2);
        await Assert.That(yCount).IsEqualTo(1);

        var y = await _context.MetricEvents.SingleAsync(e =>
            e.OrganizationId == "org_metrics_store_y"
        );
        await Assert.That(y.Id).IsGreaterThan(0L);
        await Assert.That(y.TeamId).IsEqualTo("team_y");
        await Assert.That(y.MetricType).IsEqualTo("zeeq_document_read_counter");
        await Assert.That(y.Library).IsEqualTo("zeeq-app");
        await Assert.That(y.Tags["path"]).IsEqualTo("/guide.md");
    }

    [Test]
    public async Task AppendAsync_EmptyBatch_IsNoOp()
    {
        var store = new PostgresMetricEventStore(_context);

        await store.AppendAsync([], CancellationToken.None);

        var count = await _context.MetricEvents.CountAsync(e =>
            e.OrganizationId == "org_metrics_store_empty"
        );
        await Assert.That(count).IsEqualTo(0);
    }
}
