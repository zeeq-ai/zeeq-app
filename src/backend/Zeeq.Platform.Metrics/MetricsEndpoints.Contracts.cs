using Zeeq.Core.Models;
using Microsoft.Extensions.Caching.Hybrid;

namespace Zeeq.Platform.Metrics;

/// <summary>Caller-facing error for the metrics read endpoints.</summary>
/// <param name="Code">Stable machine code, for example <c>unknown_metric_type</c>.</param>
/// <param name="Message">Human-readable explanation.</param>
public sealed record MetricsEndpointError(string Code, string Message);

/// <summary>
/// Parses the closed set of dashboard time windows from their wire tokens (<c>15m</c>, <c>24h</c>,
/// <c>7d</c>, …). Arbitrary ranges are rejected by construction so query plans stay predictable.
/// </summary>
internal static class MetricWindowQuery
{
    private static readonly IReadOnlyDictionary<string, MetricWindow> ByToken = new Dictionary<
        string,
        MetricWindow
    >(StringComparer.OrdinalIgnoreCase)
    {
        ["15m"] = MetricWindow.M15,
        ["30m"] = MetricWindow.M30,
        ["1h"] = MetricWindow.H1,
        ["4h"] = MetricWindow.H4,
        ["12h"] = MetricWindow.H12,
        ["24h"] = MetricWindow.H24,
        ["7d"] = MetricWindow.D7,
        ["14d"] = MetricWindow.D14,
        ["30d"] = MetricWindow.D30,
    };

    /// <summary>Resolves a window token, or returns false for an unknown/missing token.</summary>
    public static bool TryParse(string? token, out MetricWindow window) =>
        ByToken.TryGetValue(token ?? string.Empty, out window);
}

/// <summary>
/// The metric-type allow-sets served by the read endpoints. <c>metric_type</c> is validated against
/// these before any query runs so an unknown type is a 400, never a wasted scan.
/// </summary>
internal static class MetricTaxonomy
{
    /// <summary>Counter families valid for the bucketed series endpoint.</summary>
    public static readonly IReadOnlySet<string> SeriesTypes = new HashSet<string>(
        StringComparer.Ordinal
    )
    {
        "zeeq_tool_call_counter",
        "zeeq_user_agent_counter",
        "zeeq_document_read_counter",
        "zeeq_section_read_counter",
        "zeeq_snippet_read_counter",
        "zeeq_agent_session_counter",
        "zeeq_agent_prompt_counter",
        "zeeq_agent_tool_call_counter",
        "zeeq_agent_pr_link_counter",
        "zeeq_agent_token_usage",
        "zeeq_agent_cost_usd",
    };

    /// <summary>Histogram families valid for the percentile and scatter endpoints.</summary>
    public static readonly IReadOnlySet<string> HistogramTypes = new HashSet<string>(
        StringComparer.Ordinal
    )
    {
        "zeeq_review_duration_ms",
        "zeeq_review_tokens",
        "zeeq_review_cost_usd",
        "zeeq_agent_token_usage",
        "zeeq_agent_cost_usd",
        "zeeq_agent_cost_units_raw",
    };

    /// <summary>Read counters ranked by the UI-7 path leaderboard.</summary>
    public static readonly string[] LeaderboardTypes =
    [
        "zeeq_section_read_counter",
        "zeeq_snippet_read_counter",
    ];

    /// <summary>
    /// Section-only and snippet-only variants for the section-level leaderboard. A section and a
    /// code snippet under the same heading share an identical (path, heading) pair — see
    /// <c>ZeeqDocumentParser</c>, both text- and code-blocks derive <c>HeadingPath</c> from the
    /// same heading-tracking state — so the two kinds must be queried and ranked separately rather
    /// than combined, or reads of the explanation and reads of the code sample would merge into one
    /// row.
    /// </summary>
    public static readonly string[] SectionLeaderboardTypes = ["zeeq_section_read_counter"];

    /// <summary>See <see cref="SectionLeaderboardTypes" />.</summary>
    public static readonly string[] SnippetLeaderboardTypes = ["zeeq_snippet_read_counter"];
}

/// <summary>Shared metrics endpoint constants and cache-key helpers.</summary>
internal static class MetricsEndpointCache
{
    /// <summary>
    /// Short TTL that absorbs concurrent dashboard viewers polling the same window without letting
    /// data go visibly stale. A const, not config (resolved decision) — promote only if tuned.
    /// </summary>
    public static readonly HybridCacheEntryOptions Options = new()
    {
        Expiration = TimeSpan.FromSeconds(30),
    };

    /// <summary>Builds a deterministic cache key from an org, route, and normalized parts.</summary>
    public static string Key(string organizationId, string route, params string?[] parts) =>
        $"metrics:{organizationId}:{route}:{string.Join('|', parts)}";

    /// <summary>Joins a multi-select filter into a stable, order-insensitive key fragment.</summary>
    public static string Join(string[]? values) =>
        values is { Length: > 0 }
            ? string.Join(
                ',',
                values.Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal)
            )
            : string.Empty;
}
