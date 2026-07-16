using Zeeq.Core.Models;
using Zeeq.Testing;
using Microsoft.EntityFrameworkCore;

namespace Zeeq.Data.Postgres.Tests;

/// <summary>
/// Integration tests for the <see cref="MetricEvent" /> data model — jsonb tag
/// round-trip, sparse null promoted columns, batch insert (the write path's real
/// shape), and index configuration guarding against silent mapping drift.
///
/// Run:
/// dotnet run --project src/backend/Zeeq.Data.Postgres.Tests --output detailed --disable-logo --treenode-filter "/*/*/MetricEventDataModelIntegrationTests/*"
/// </summary>
[Property("integration", "true")]
[Property("testcontainer", "true")]
[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class MetricEventDataModelIntegrationTests(PgDatabaseFixture postgres)
    : PgTransactionalTestBase(postgres)
{
    [Test]
    public async Task MetricEvent_InsertAndReadBack_RoundTripsAllColumns()
    {
        // Guards that every promoted column and the jsonb tag bag survive a
        // full write/read cycle, and that the identity id is assigned on insert.
        var createdAt = DateTimeOffset.UtcNow;
        var metricEvent = new MetricEvent
        {
            OrganizationId = "org_metrics_roundtrip",
            TeamId = "team_alpha",
            MetricType = "zeeq_tool_call_counter",
            MetricValue = 1,
            UserEmail = "user@example.com",
            ToolName = "search_documents",
            RepositoryId = "repo_alpha",
            Library = "zeeq-app",
            Facet = "security",
            Tags = new() { ["user_agent"] = "opencode/1.0", ["agent_client"] = "cli" },
            CreatedAtUtc = createdAt,
        };

        _context.MetricEvents.Add(metricEvent);
        await _context.SaveChangesAsync();
        var id = metricEvent.Id;
        _context.ChangeTracker.Clear();

        var saved = await _context.MetricEvents.SingleAsync(e => e.Id == id);
        await Assert.That(saved.Id).IsGreaterThan(0L);
        await Assert.That(saved.OrganizationId).IsEqualTo("org_metrics_roundtrip");
        await Assert.That(saved.TeamId).IsEqualTo("team_alpha");
        await Assert.That(saved.MetricType).IsEqualTo("zeeq_tool_call_counter");
        await Assert.That(saved.MetricValue).IsEqualTo(1d);
        await Assert.That(saved.UserEmail).IsEqualTo("user@example.com");
        await Assert.That(saved.ToolName).IsEqualTo("search_documents");
        await Assert.That(saved.RepositoryId).IsEqualTo("repo_alpha");
        await Assert.That(saved.Library).IsEqualTo("zeeq-app");
        await Assert.That(saved.Facet).IsEqualTo("security");
        await Assert.That(saved.Tags.Count).IsEqualTo(2);
        await Assert.That(saved.Tags["user_agent"]).IsEqualTo("opencode/1.0");
        await Assert.That(saved.Tags["agent_client"]).IsEqualTo("cli");
        await Assert.That(saved.CreatedAtUtc).IsEqualTo(createdAt.TruncateToPostgresPrecision());
    }

    [Test]
    public async Task MetricEvent_InsertWithNullPromotedColumns_RoundTripsNullsAndEmptyTags()
    {
        // Guards the sparse wide-event shape: a metric family that only populates
        // the required columns persists nulls (not empty strings) and an empty tag bag.
        var metricEvent = new MetricEvent
        {
            OrganizationId = "org_metrics_sparse",
            MetricType = "zeeq_review_tokens",
            MetricValue = 1234,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        _context.MetricEvents.Add(metricEvent);
        await _context.SaveChangesAsync();
        var id = metricEvent.Id;
        _context.ChangeTracker.Clear();

        var saved = await _context.MetricEvents.SingleAsync(e => e.Id == id);
        await Assert.That(saved.TeamId).IsNull();
        await Assert.That(saved.UserEmail).IsNull();
        await Assert.That(saved.ToolName).IsNull();
        await Assert.That(saved.RepositoryId).IsNull();
        await Assert.That(saved.Library).IsNull();
        await Assert.That(saved.Facet).IsNull();
        await Assert.That(saved.Tags).IsNotNull();
        await Assert.That(saved.Tags.Count).IsEqualTo(0);
        await Assert.That(saved.MetricValue).IsEqualTo(1234d);
    }

    [Test]
    public async Task MetricEvent_BulkInsert_PersistsBatch()
    {
        // Mirrors the write path's real shape: AddRange + a single SaveChangesAsync
        // of a full 1,000-sample batch. Asserts the count and a spot-checked row.
        const int batchSize = 1000;
        const string organizationId = "org_metrics_bulk";
        var createdAt = DateTimeOffset.UtcNow;

        var events = Enumerable
            .Range(0, batchSize)
            .Select(index => new MetricEvent
            {
                OrganizationId = organizationId,
                MetricType = "zeeq_document_read_counter",
                MetricValue = 1,
                UserEmail = $"user{index}@example.com",
                ToolName = "read_document_by_path",
                Library = "zeeq-app",
                Tags = new() { ["path"] = $"/doc/{index}.md" },
                CreatedAtUtc = createdAt,
            })
            .ToList();

        _context.MetricEvents.AddRange(events);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var count = await _context.MetricEvents.CountAsync(e =>
            e.OrganizationId == organizationId
        );
        await Assert.That(count).IsEqualTo(batchSize);

        var spot = await _context.MetricEvents.SingleAsync(e =>
            e.OrganizationId == organizationId && e.UserEmail == "user500@example.com"
        );
        await Assert.That(spot.MetricType).IsEqualTo("zeeq_document_read_counter");
        await Assert.That(spot.Tags["path"]).IsEqualTo("/doc/500.md");
    }

    [Test]
    public async Task MetricEvent_ModelConfiguration_DeclaresOrgTypePrefixedIndexes()
    {
        // Guards against silent index-config drift: all six indexes must lead with
        // (organization_id, metric_type), end with created_at_utc, and the five
        // sparse-dimension indexes must carry their IS NOT NULL partial filters.
        var entityType = _context.Model.FindEntityType(typeof(MetricEvent));
        await Assert.That(entityType).IsNotNull();

        var indexes = entityType!.GetIndexes().ToList();
        await Assert.That(indexes.Count).IsEqualTo(6);

        foreach (var index in indexes)
        {
            var propertyNames = index.Properties.Select(property => property.Name).ToList();
            await Assert.That(propertyNames[0]).IsEqualTo(nameof(MetricEvent.OrganizationId));
            await Assert.That(propertyNames[1]).IsEqualTo(nameof(MetricEvent.MetricType));
            await Assert.That(propertyNames[^1]).IsEqualTo(nameof(MetricEvent.CreatedAtUtc));
        }

        var filters = indexes
            .Select(index => index.GetFilter())
            .Where(filter => filter is not null)
            .ToList();
        await Assert.That(filters.Count).IsEqualTo(5);
        await Assert.That(filters).Contains("user_email IS NOT NULL");
        await Assert.That(filters).Contains("tool_name IS NOT NULL");
        await Assert.That(filters).Contains("library IS NOT NULL");
        await Assert.That(filters).Contains("repository_id IS NOT NULL");
        await Assert.That(filters).Contains("facet IS NOT NULL");
    }
}
