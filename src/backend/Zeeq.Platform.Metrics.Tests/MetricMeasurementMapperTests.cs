using Zeeq.Platform.Metrics;

namespace Zeeq.Platform.Metrics.Tests;

/// <summary>
/// Unit tests for <see cref="MetricMeasurementMapper" /> — tag promotion, residual capture, and
/// the organization_id capture rule. Pure, side-effect-free mapping; no listener or channel.
/// </summary>
public sealed class MetricMeasurementMapperTests
{
    [Test]
    public async Task TryCreate_MapsPromotedTagsAndResiduals()
    {
        var capturedAt = DateTimeOffset.UtcNow;
        KeyValuePair<string, object?>[] tags =
        [
            new("organization_id", "org_1"),
            new("team_id", "team_1"),
            new("user", "u@example.com"),
            new("tool_name", "search_documents"),
            new("repository_id", "repo_1"),
            new("library", "zeeq-app"),
            new("facet", "security"),
            new("user_agent", "opencode/1.0"),
            new("path", "/doc.md"),
        ];

        var sample = MetricMeasurementMapper.TryCreate(
            "zeeq_tool_call_counter",
            1,
            tags,
            capturedAt
        );

        await Assert.That(sample).IsNotNull();
        await Assert.That(sample!.OrganizationId).IsEqualTo("org_1");
        await Assert.That(sample.TeamId).IsEqualTo("team_1");
        await Assert.That(sample.MetricType).IsEqualTo("zeeq_tool_call_counter");
        await Assert.That(sample.MetricValue).IsEqualTo(1d);
        await Assert.That(sample.UserEmail).IsEqualTo("u@example.com");
        await Assert.That(sample.ToolName).IsEqualTo("search_documents");
        await Assert.That(sample.RepositoryId).IsEqualTo("repo_1");
        await Assert.That(sample.Library).IsEqualTo("zeeq-app");
        await Assert.That(sample.Facet).IsEqualTo("security");
        await Assert.That(sample.CapturedAtUtc).IsEqualTo(capturedAt);
        await Assert.That(sample.Tags).IsNotNull();
        await Assert.That(sample.Tags!.Count).IsEqualTo(2);
        await Assert.That(sample.Tags["user_agent"]).IsEqualTo("opencode/1.0");
        await Assert.That(sample.Tags["path"]).IsEqualTo("/doc.md");
    }

    [Test]
    public async Task TryCreate_WithNoResidualTags_LeavesTagsNull()
    {
        KeyValuePair<string, object?>[] tags =
        [
            new("organization_id", "org_1"),
            new("tool_name", "list_libraries"),
        ];

        var sample = MetricMeasurementMapper.TryCreate(
            "zeeq_tool_call_counter",
            1,
            tags,
            DateTimeOffset.UtcNow
        );

        await Assert.That(sample).IsNotNull();
        await Assert.That(sample!.Tags).IsNull();
        await Assert.That(sample.ToolName).IsEqualTo("list_libraries");
    }

    [Test]
    public async Task TryCreate_WithoutOrganizationId_ReturnsNull()
    {
        KeyValuePair<string, object?>[] tags =
        [
            new("user", "u@example.com"),
            new("tool_name", "list_libraries"),
        ];

        var sample = MetricMeasurementMapper.TryCreate(
            "zeeq_tool_call_counter",
            1,
            tags,
            DateTimeOffset.UtcNow
        );

        await Assert.That(sample).IsNull();
    }

    [Test]
    public async Task TryCreate_WithEmptyOrganizationId_ReturnsNull()
    {
        KeyValuePair<string, object?>[] tags =
        [
            new("organization_id", ""),
            new("tool_name", "list_libraries"),
        ];

        var sample = MetricMeasurementMapper.TryCreate(
            "zeeq_tool_call_counter",
            1,
            tags,
            DateTimeOffset.UtcNow
        );

        await Assert.That(sample).IsNull();
    }
}
