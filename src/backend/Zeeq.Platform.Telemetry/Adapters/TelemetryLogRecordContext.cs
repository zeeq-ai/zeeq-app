using System.Globalization;
using Zeeq.Platform.Telemetry.Adapters.ClaudeCode;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Resource.V1;
using OtlpLog = OpenTelemetry.Proto.Logs.V1;

namespace Zeeq.Platform.Telemetry.Adapters;

/// <summary>
/// Resource, scope, and log-record context for one OTLP log record.
/// </summary>
/// <remarks>
/// Codex session fields are split across resource attributes, scope names, and
/// log-record attributes. This type gives adapters one stable view while keeping
/// protobuf traversal outside individual parser branches.
/// </remarks>
public sealed class TelemetryLogRecordContext(
    Resource resource,
    InstrumentationScope scope,
    OtlpLog.LogRecord logRecord
)
{
    private readonly Dictionary<string, AnyValue> _resourceAttrs = ToAttributeMap(
        resource.Attributes
    );
    private readonly Dictionary<string, AnyValue> _logAttrs = ToAttributeMap(logRecord.Attributes);

    /// <summary>OTLP resource.</summary>
    public Resource Resource { get; } = resource;

    /// <summary>OTLP instrumentation scope.</summary>
    public InstrumentationScope Scope { get; } = scope;

    /// <summary>OTLP log record.</summary>
    public OtlpLog.LogRecord LogRecord { get; } = logRecord;

    /// <summary>
    /// The <c>service.name</c> from the resource.
    /// </summary>
    public string ServiceName => ResourceString("service.name") ?? "";

    /// <summary>
    /// Enumerates every log record in an OTLP logs request with resource and scope context.
    /// </summary>
    public static IEnumerable<TelemetryLogRecordContext> Enumerate(ExportLogsServiceRequest request)
    {
        foreach (var resourceLog in request.ResourceLogs)
        {
            var res = resourceLog.Resource ?? new Resource();

            foreach (var scopeLog in resourceLog.ScopeLogs)
            {
                var scp = scopeLog.Scope ?? new InstrumentationScope();

                foreach (var lr in scopeLog.LogRecords)
                {
                    yield return new(res, scp, lr);
                }
            }
        }
    }

    /// <summary>Reads a string-valued log attribute.</summary>
    public string? LogString(string key) =>
        _logAttrs.TryGetValue(key, out var v) ? ValueAsString(v) : null;

    /// <summary>Reads a string-valued resource attribute.</summary>
    public string? ResourceString(string key) =>
        _resourceAttrs.TryGetValue(key, out var v) ? ValueAsString(v) : null;

    /// <summary>Reads the log record body as a string.</summary>
    public string? BodyString() => ValueAsString(LogRecord.Body);

    /// <summary>Reads a 32-bit integer-valued log attribute.</summary>
    public int? LogInt32(string key)
    {
        if (!_logAttrs.TryGetValue(key, out var v))
        {
            return null;
        }

        return ValueAsInt32(v);
    }

    /// <summary>Reads a boolean-valued log attribute.</summary>
    public bool? LogBool(string key)
    {
        if (!_logAttrs.TryGetValue(key, out var v))
        {
            return null;
        }

        return v.ValueCase switch
        {
            AnyValue.ValueOneofCase.BoolValue => v.BoolValue,
            AnyValue.ValueOneofCase.StringValue when bool.TryParse(v.StringValue, out var parsed) =>
                parsed,
            _ => null,
        };
    }

    /// <summary>Returns whether a log attribute key exists.</summary>
    public bool HasLogAttribute(string key) => _logAttrs.ContainsKey(key);

    /// <summary>Reads a 64-bit integer-valued log attribute.</summary>
    public long? LogInt64(string key)
    {
        if (!_logAttrs.TryGetValue(key, out var v))
        {
            return null;
        }

        return v.ValueCase switch
        {
            AnyValue.ValueOneofCase.IntValue => v.IntValue,
            AnyValue.ValueOneofCase.StringValue when long.TryParse(v.StringValue, out var parsed) =>
                parsed,
            _ => null,
        };
    }

    /// <summary>
    /// Reads the event timestamp from the <c>event.timestamp</c> attribute, falling
    /// back to OTLP nanosecond timestamps.
    /// </summary>
    public DateTimeOffset ReadEventTimestampUtc(string timestampKey)
    {
        var ts = LogString(timestampKey);
        if (
            DateTimeOffset.TryParse(
                ts,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed
            )
        )
        {
            return parsed;
        }

        if (LogRecord.TimeUnixNano > 0)
        {
            return FromUnixNanoseconds(LogRecord.TimeUnixNano);
        }

        if (LogRecord.ObservedTimeUnixNano > 0)
        {
            return FromUnixNanoseconds(LogRecord.ObservedTimeUnixNano);
        }

        return DateTimeOffset.UnixEpoch;
    }

    // --- Claude helpers ---

    /// <summary>Returns <c>true</c> when the record originates from Claude Code.</summary>
    public bool HasClaudeCodeIdentity() =>
        ServiceName == ClaudeCodeLogAttributes.HarnessName
        || Scope.Name.Contains(
            ClaudeCodeLogAttributes.SourceName,
            StringComparison.OrdinalIgnoreCase
        );

    /// <summary>Reads the Claude Code event name from the body or <c>event.name</c> attribute.</summary>
    public string? ReadClaudeCodeEventName()
    {
        var body = BodyString();
        if (ClaudeCodeLogAttributes.IsClaudeCodeEventName(body))
        {
            return body;
        }

        return LogString("event.name");
    }

    // --- Codex helpers ---

    /// <summary>Returns <c>true</c> when the record matches Codex session heuristics.</summary>
    public bool LooksLikeCodexSessionRecord()
    {
        var serviceName = ServiceName;
        var scopeName = Scope.Name;

        return serviceName.Contains("codex", StringComparison.OrdinalIgnoreCase)
            || scopeName.Contains("codex", StringComparison.OrdinalIgnoreCase)
            || HasCodexFallbackShape();
    }

    /// <summary>Returns <c>true</c> when this is a Codex user prompt record.</summary>
    public bool IsCodexUserPrompt() =>
        HasLogAttribute("prompt_length")
        && !string.IsNullOrWhiteSpace(LogString("conversation.id"));

    /// <summary>
    /// Fixed v2 detection: no <c>event.name</c> on the wire; use the
    /// two-session-validated fallback — both <c>mcp_servers</c> and
    /// <c>approval_policy</c> must be present.
    /// </summary>
    public bool IsCodexConversationStarted() =>
        HasLogAttribute("mcp_servers") && HasLogAttribute("approval_policy");

    /// <summary>Returns <c>true</c> when this is a Codex tool result record.</summary>
    public bool IsCodexToolResult() => HasLogAttribute("tool_name") && HasLogAttribute("call_id");

    /// <summary>Returns <c>true</c> when this is a Codex response-completed record.</summary>
    public bool IsCodexResponseCompleted() =>
        string.Equals(LogString("event.kind"), "response.completed", StringComparison.Ordinal);

    private bool HasCodexFallbackShape() =>
        !string.IsNullOrWhiteSpace(LogString("conversation.id"))
        && (
            HasLogAttribute("prompt_length")
            || (HasLogAttribute("tool_name") && HasLogAttribute("call_id"))
            || IsCodexResponseCompleted()
        );

    // --- General helpers ---

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
            { ValueCase: AnyValue.ValueOneofCase.IntValue } => value.IntValue.ToString(
                CultureInfo.InvariantCulture
            ),
            { ValueCase: AnyValue.ValueOneofCase.DoubleValue } => value.DoubleValue.ToString(
                CultureInfo.InvariantCulture
            ),
            { ValueCase: AnyValue.ValueOneofCase.BoolValue } => value.BoolValue.ToString(),
            _ => null,
        };

    private static int? ValueAsInt32(AnyValue value) =>
        value.ValueCase switch
        {
            AnyValue.ValueOneofCase.IntValue
                when value.IntValue is >= int.MinValue and <= int.MaxValue => (int)value.IntValue,
            AnyValue.ValueOneofCase.StringValue
                when int.TryParse(
                    value.StringValue,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var r
                ) => r,
            _ => null,
        };

    private static DateTimeOffset FromUnixNanoseconds(ulong unixNanoseconds) =>
        DateTimeOffset.UnixEpoch.AddTicks((long)(unixNanoseconds / 100));
}
