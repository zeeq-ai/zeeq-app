using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace Zeeq.Platform.Telemetry.Adapters;

/// <summary>
/// Resource and span context for one OTLP span record — the span analogue of
/// <see cref="TelemetryLogRecordContext"/>. Used by the Copilot Chat adapter.
/// </summary>
public sealed class TelemetrySpanRecordContext(Span span, Resource resource)
{
    private readonly Dictionary<string, AnyValue> _resourceAttrs = ToAttributeMap(
        resource.Attributes
    );
    private readonly Dictionary<string, AnyValue> _spanAttrs = ToAttributeMap(span.Attributes);

    /// <summary>OTLP span.</summary>
    public Span Span { get; } = span;

    /// <summary>OTLP resource.</summary>
    public Resource Resource { get; } = resource;

    /// <summary>Trace identifier as a hex string.</summary>
    public string TraceId => Span.TraceId.ToStringUtf8();

    /// <summary>Span identifier as a hex string.</summary>
    public string SpanId => Span.SpanId.ToStringUtf8();

    /// <summary>
    /// The <c>gen_ai.operation.name</c> from span attributes.
    /// </summary>
    public string OperationName => SpanString("gen_ai.operation.name") ?? "";

    /// <summary>
    /// The <c>service.name</c> from the resource.
    /// </summary>
    public string ServiceName => ResourceString("service.name") ?? "";

    /// <summary>
    /// The <c>copilot_chat.chat_session_id</c> from span attributes.
    /// </summary>
    public string ChatSessionId => SpanString("copilot_chat.chat_session_id") ?? "";

    /// <summary>
    /// Span start time as UTC <c>DateTimeOffset</c>.
    /// </summary>
    public DateTimeOffset StartedAtUtc =>
        DateTimeOffset.FromUnixTimeMilliseconds(NanosToMillis(Span.StartTimeUnixNano));

    /// <summary>
    /// Span duration in nanoseconds.
    /// </summary>
    public long DurationNanos => (long)(Span.EndTimeUnixNano - Span.StartTimeUnixNano);

    /// <summary>
    /// Span status code.
    /// </summary>
    public Status.Types.StatusCode SpanStatus => Span.Status?.Code ?? Status.Types.StatusCode.Unset;

    /// <summary>Reads a string-valued span attribute.</summary>
    public string? SpanString(string key) =>
        _spanAttrs.TryGetValue(key, out var v) ? ValueAsString(v) : null;

    /// <summary>Reads a string-valued resource attribute.</summary>
    public string? ResourceString(string key) =>
        _resourceAttrs.TryGetValue(key, out var v) ? ValueAsString(v) : null;

    /// <summary>Reads a 32-bit integer-valued span attribute.</summary>
    public int? SpanInt32(string key)
    {
        if (!_spanAttrs.TryGetValue(key, out var v))
        {
            return null;
        }

        return v.ValueCase switch
        {
            AnyValue.ValueOneofCase.IntValue
                when v.IntValue is >= int.MinValue and <= int.MaxValue => (int)v.IntValue,
            AnyValue.ValueOneofCase.StringValue when int.TryParse(v.StringValue, out var r) => r,
            _ => null,
        };
    }

    /// <summary>Reads a 64-bit integer-valued span attribute.</summary>
    public long? SpanInt64(string key)
    {
        if (!_spanAttrs.TryGetValue(key, out var v))
        {
            return null;
        }

        return v.ValueCase switch
        {
            AnyValue.ValueOneofCase.IntValue => v.IntValue,
            AnyValue.ValueOneofCase.StringValue when long.TryParse(v.StringValue, out var r) => r,
            _ => null,
        };
    }

    private static Dictionary<string, AnyValue> ToAttributeMap(IEnumerable<KeyValue> attributes)
    {
        var result = new Dictionary<string, AnyValue>(StringComparer.Ordinal);
        foreach (var a in attributes)
        {
            if (a.Value is not null)
            {
                result[a.Key] = a.Value;
            }
        }

        return result;
    }

    private static string? ValueAsString(AnyValue? value) =>
        value switch
        {
            null => null,
            { ValueCase: AnyValue.ValueOneofCase.StringValue } => value.StringValue,
            { ValueCase: AnyValue.ValueOneofCase.IntValue } => value.IntValue.ToString(),
            { ValueCase: AnyValue.ValueOneofCase.BoolValue } => value.BoolValue.ToString(),
            _ => null,
        };

    private static long NanosToMillis(ulong nanoseconds) => (long)(nanoseconds / 1_000_000);
}
