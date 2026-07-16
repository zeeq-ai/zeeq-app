using System.Diagnostics.Metrics;
using Zeeq.Core.Models;

namespace Zeeq.Platform.Metrics;

/// <summary>
/// Pure mapping from a <see cref="MeterListener" /> measurement to a <see cref="MetricSample" />.
/// </summary>
/// <remarks>
/// Extracted from the hosted service so the tag-promotion and capture-rule logic is unit-testable
/// without a running listener or channel. Side-effect free: it only reads the tag span and returns
/// a sample (or null when the capture rule rejects it).
/// </remarks>
internal static class MetricMeasurementMapper
{
    // Tag names are exact strings shared with the instrumentation call sites.
    public const string OrganizationIdTag = "organization_id";
    public const string TeamIdTag = "team_id";
    public const string UserTag = "user";
    public const string ToolNameTag = "tool_name";
    public const string RepositoryIdTag = "repository_id";
    public const string LibraryTag = "library";
    public const string FacetTag = "facet";

    /// <summary>
    /// Maps one measurement to a sample, or returns null when the capture rule rejects it.
    /// </summary>
    /// <remarks>
    /// The capture rule: a measurement is persisted only when it carries a non-empty
    /// <c>organization_id</c> tag. Known tag names are promoted to their columns; everything else
    /// lands in <see cref="MetricSample.Tags" />.
    /// </remarks>
    public static MetricSample? TryCreate(
        string metricType,
        double value,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        DateTimeOffset capturedAtUtc
    )
    {
        string? organizationId = null;
        string? teamId = null;
        string? userEmail = null;
        string? toolName = null;
        string? repositoryId = null;
        string? library = null;
        string? facet = null;
        Dictionary<string, string>? residualTags = null;

        foreach (var tag in tags)
        {
            var tagValue = tag.Value?.ToString();
            switch (tag.Key)
            {
                case OrganizationIdTag:
                    organizationId = tagValue;
                    break;
                case TeamIdTag:
                    teamId = tagValue;
                    break;
                case UserTag:
                    userEmail = tagValue;
                    break;
                case ToolNameTag:
                    toolName = tagValue;
                    break;
                case RepositoryIdTag:
                    repositoryId = tagValue;
                    break;
                case LibraryTag:
                    library = tagValue;
                    break;
                case FacetTag:
                    facet = tagValue;
                    break;
                default:
                    if (tagValue is not null)
                    {
                        (residualTags ??= [])[tag.Key] = tagValue;
                    }
                    break;
            }
        }

        // Capture rule: no organization scope → not persisted (still flows to OTEL).
        if (string.IsNullOrEmpty(organizationId))
        {
            return null;
        }

        return new MetricSample(
            organizationId,
            teamId,
            metricType,
            value,
            userEmail,
            toolName,
            repositoryId,
            library,
            facet,
            residualTags,
            capturedAtUtc
        );
    }
}
