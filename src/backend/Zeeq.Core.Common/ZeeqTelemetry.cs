using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;

namespace Zeeq.Core.Common;

/// <summary>
/// Static class for Zeeq telemetry
/// </summary>
public static class ZeeqTelemetry
{
    /// <summary>
    /// The telemetry activity source name.
    /// </summary>
    public const string ActivitySourceName = "Zeeq";

    /// <summary>
    /// The ActivitySource for Zeeq telemetry
    /// See: https://opentelemetry.io/docs/languages/dotnet/traces/best-practices/#activitysource
    /// </summary>
    public static readonly ActivitySource Tracer = new(ActivitySourceName);

    /// <summary>
    /// The Meter for Zeeq telemetry
    /// See:https://opentelemetry.io/docs/languages/dotnet/metrics/best-practices/#meter
    /// </summary>
    public static readonly Meter Metrics = new(ActivitySourceName, "1.0");

    /// <summary>
    /// Convenience method to start an activity with just tags.
    /// </summary>
    /// <param name="tags">A set of key-value pairs</param>
    /// <param name="traceName">
    /// The name of the trace or null to use the default naming convention.
    /// </param>
    /// <param name="memberName">The caller member name.</param>
    /// <param name="filePath">The caller file path.</param>
    /// <param name="lineNumber">The caller line number.</param>
    /// <returns>The new activity.</returns>
    public static Activity? Trace(
        (string Key, object? Value)[] tags,
        string? traceName = null,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int lineNumber = 0
    ) =>
        ZeeqTelemetry.Tracer.StartActivity(
            $"{traceName ?? $"{Path.GetFileName(filePath)}#{memberName}"}",
            kind: ActivityKind.Internal,
            parentContext: Activity.Current?.Context ?? default,
            tags:
            [
                .. tags.Select(tag => new KeyValuePair<string, object?>(tag.Key, tag.Value)),
                new KeyValuePair<string, object?>("code.member", memberName),
                new KeyValuePair<string, object?>("code.file_path", filePath),
                new KeyValuePair<string, object?>("code.line_number", lineNumber),
            ]
        );

    /// <summary>
    /// Starts a root activity even when an upstream transport activity is current.
    /// </summary>
    /// <remarks>
    /// MCP tool calls often run under a generic transport span such as
    /// <c>POST /mcp</c>. Domain workflows that need first-class trace discovery
    /// can use this helper to start a top-level trace while still tagging the
    /// operation with source-code metadata.
    /// </remarks>
    /// <param name="tags">A set of key-value pairs.</param>
    /// <param name="traceName">The activity name.</param>
    /// <param name="additionalLinks">Additional causal contexts to link without making them parents.</param>
    /// <param name="memberName">The caller member name.</param>
    /// <param name="filePath">The caller file path.</param>
    /// <param name="lineNumber">The caller line number.</param>
    /// <returns>The new root activity.</returns>
    public static ZeeqActivityScope TraceRoot(
        (string Key, object? Value)[] tags,
        string traceName,
        IEnumerable<ActivityLink>? additionalLinks = null,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int lineNumber = 0
    )
    {
        var previousActivity = Activity.Current;
        Activity.Current = null;

        var activity = ZeeqTelemetry.Tracer.StartActivity(
            traceName,
            kind: ActivityKind.Internal,
            parentContext: default,
            tags:
            [
                .. tags.Select(tag => new KeyValuePair<string, object?>(tag.Key, tag.Value)),
                new KeyValuePair<string, object?>("code.member", memberName),
                new KeyValuePair<string, object?>("code.file_path", filePath),
                new KeyValuePair<string, object?>("code.line_number", lineNumber),
            ],
            links: [.. CreateLinks(previousActivity), .. additionalLinks ?? []]
        );

        if (activity is null)
        {
            Activity.Current = previousActivity;
        }

        return new(activity, previousActivity);
    }

    /// <summary>
    /// Adds an event with tuple-style tags to the current activity.
    /// </summary>
    /// <param name="tags">A set of key-value pairs.</param>
    /// <param name="eventName">An optional event name (formulated from the caller member name if not provided).</param>
    /// <param name="name">The caller member name.</param>
    /// <param name="filePath">The caller file path added as event metadata.</param>
    /// <param name="lineNumber">The caller line number added as event metadata.</param>
    /// <returns>The current activity.</returns>
    public static Activity? AddEvent(
        (string Key, object? Value)[] tags,
        string? eventName = null,
        [CallerMemberName] string name = "",
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int lineNumber = 0
    )
    {
        var activity = Activity.Current;

        var effectiveName = eventName ?? $"{name}@{Path.GetFileName(filePath)}:{lineNumber}";

        activity?.AddEvent(
            new ActivityEvent(
                effectiveName,
                tags:
                [
                    .. tags.Select(tag => new KeyValuePair<string, object?>(tag.Key, tag.Value)),
                    new KeyValuePair<string, object?>("code.member", name),
                    new KeyValuePair<string, object?>("code.file_path", filePath),
                    new KeyValuePair<string, object?>("code.line_number", lineNumber),
                ]
            )
        );

        return activity;
    }

    /// <summary>
    /// Captures the current W3C trace context in a serializable carrier.
    /// </summary>
    /// <remarks>
    /// MCP expert-review upload URLs cross process and protocol boundaries where
    /// normal trace headers cannot flow. This carrier stores only the standard
    /// trace context strings so a later request can continue the same workflow.
    /// </remarks>
    /// <returns>The current trace context, or an empty context when no activity is active.</returns>
    public static ZeeqTraceContext CaptureCurrentTraceContext() =>
        new(Activity.Current?.Id, Activity.Current?.TraceStateString);

    /// <summary>
    /// Attempts to parse a serialized W3C trace context into an activity parent.
    /// </summary>
    /// <param name="traceContext">The serialized trace context.</param>
    /// <param name="activityContext">The parsed activity context.</param>
    /// <returns><c>true</c> when the trace context is present and valid.</returns>
    public static bool TryParseTraceContext(
        ZeeqTraceContext traceContext,
        out ActivityContext activityContext
    )
    {
        activityContext = default;

        if (string.IsNullOrWhiteSpace(traceContext.TraceParent))
        {
            return false;
        }

        return ActivityContext.TryParse(
            traceContext.TraceParent,
            traceContext.TraceState,
            isRemote: true,
            out activityContext
        );
    }

    /// <summary>
    /// Sets attributes for the current activity.
    /// </summary>
    /// <param name="tags">The key-value pair of attributes to set.</param>
    public static void SetTags(params (string Key, object? Value)[] tags)
    {
        foreach (var (Key, Value) in tags)
        {
            Activity.Current?.SetTag(Key, Value);
        }
    }

    private static ActivityLink[] CreateLinks(Activity? activity)
    {
        if (activity is null)
        {
            return [];
        }

        return activity.Context.TraceId != default && activity.Context.SpanId != default
            ? [new ActivityLink(activity.Context)]
            : [];
    }
}

/// <summary>
/// Disposable scope for a root activity that temporarily suppresses the ambient parent.
/// </summary>
/// <remarks>
/// <c>ActivitySource.StartActivity</c> can still inherit
/// <see cref="Activity.Current" /> when passed a default
/// parent context. This scope preserves the previous ambient activity, exposes
/// the root activity while active, and restores the previous activity on dispose.
/// </remarks>
public sealed class ZeeqActivityScope(Activity? activity, Activity? previousActivity)
    : IDisposable
{
    /// <summary>
    /// The started root activity, or null when no listener is active.
    /// </summary>
    public Activity? Activity { get; } = activity;

    /// <inheritdoc />
    public void Dispose()
    {
        Activity?.Dispose();
        Activity.Current = previousActivity;
    }
}

/// <summary>
/// Serializable W3C trace context used to continue workflows across disjointed requests.
/// </summary>
/// <remarks>
/// The values correspond to the standard <c>traceparent</c> and <c>tracestate</c>
/// fields. They are diagnostic metadata only; application correctness must not
/// depend on their presence.
/// </remarks>
/// <param name="TraceParent">The W3C <c>traceparent</c> value.</param>
/// <param name="TraceState">The W3C <c>tracestate</c> value.</param>
public sealed record ZeeqTraceContext(string? TraceParent, string? TraceState);

/// <summary>
/// Static extension methods for convenience.
/// </summary>
public static class ZeeqTelemetryExtensions
{
    extension(Activity? activity)
    {
        /// <summary>
        /// Convenience method to add an event to an activity with just tags.
        /// </summary>
        /// <param name="tags">A set of key-value pairs</param>
        /// <param name="name">The name of the event or the caller member name if not specified.</param>
        /// <returns>The current activity.</returns>
        public Activity? AddEvent(
            (string Key, object? Value)[] tags,
            [CallerMemberName] string name = ""
        )
        {
            activity?.AddEvent(
                new ActivityEvent(
                    name,
                    tags:
                    [
                        .. tags.Select(tag => new KeyValuePair<string, object?>(
                            tag.Key,
                            tag.Value
                        )),
                    ]
                )
            );

            return activity;
        }
    }

    extension(Counter<int> counter)
    {
        /// <summary>
        /// Increments the counter by the specified value and adds metadata tags.
        /// </summary>
        /// <param name="value">The value to increment by (defaults to 1)</param>
        /// <param name="tags">An array of key-value pairs to add as metadata tags</param>
        public void Increment(int value = 1, (string Key, object? Value)[]? tags = null)
        {
            counter.Add(
                value,
                new ReadOnlySpan<KeyValuePair<string, object?>>(
                    tags?.Select(t => new KeyValuePair<string, object?>(t.Key, t.Value)).ToArray()
                        ?? []
                )
            );
        }
    }

    extension<TNumeric>(Histogram<TNumeric> histogram)
        where TNumeric : unmanaged
    {
        /// <summary>
        /// Records a value in the histogram and adds metadata tags.
        /// </summary>
        /// <param name="value">The numeric value to record</param>
        /// <param name="tags">An array of key-value pairs to add as metadata tags</param>
        public void Record(TNumeric value, (string Key, object? Value)[]? tags = null)
        {
            histogram.Record(
                value,
                new ReadOnlySpan<KeyValuePair<string, object?>>(
                    tags?.Select(t => new KeyValuePair<string, object?>(t.Key, t.Value)).ToArray()
                        ?? []
                )
            );
        }
    }
}
