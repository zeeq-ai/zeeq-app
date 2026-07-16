using Zeeq.Core.Models;

namespace Zeeq.Data.Postgres.Metrics;

/// <summary>
/// Postgres write store for captured metric measurements.
/// </summary>
/// <remarks>
/// Maps each <see cref="MetricSample" /> to a <see cref="MetricEvent" /> row and
/// persists the whole batch with a single <c>SaveChangesAsync</c> — one round
/// trip per Brighter batch message, matching the write path's design. No query
/// tag here because this is an insert-only path (read queries in the metrics
/// query store carry <c>TagWithOperationCallSite</c>).
/// </remarks>
internal sealed class PostgresMetricEventStore(PostgresDbContext db) : IMetricEventStore
{
    /// <inheritdoc />
    public async Task AppendAsync(
        IReadOnlyList<MetricSample> samples,
        CancellationToken cancellationToken
    )
    {
        if (samples.Count == 0)
        {
            return;
        }

        var events = new List<MetricEvent>(samples.Count);
        foreach (var sample in samples)
        {
            events.Add(
                new MetricEvent
                {
                    OrganizationId = sample.OrganizationId,
                    TeamId = sample.TeamId,
                    MetricType = sample.MetricType,
                    MetricValue = sample.MetricValue,
                    UserEmail = sample.UserEmail,
                    ToolName = sample.ToolName,
                    RepositoryId = sample.RepositoryId,
                    Library = sample.Library,
                    Facet = sample.Facet,
                    Tags = sample.Tags ?? [],
                    CreatedAtUtc = sample.CapturedAtUtc,
                }
            );
        }

        db.MetricEvents.AddRange(events);
        await db.SaveChangesAsync(cancellationToken);
    }
}
