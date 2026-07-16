using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace Zeeq.Platform.Telemetry.Tests;

/// <summary>
/// Helpers for building OTLP protobuf test fixtures from attribute maps.
/// </summary>
internal static class TestTelemetry
{
    public static Resource Resource(string serviceName, params (string Key, string Value)[] attrs)
    {
        var resource = new Resource();
        resource.Attributes.Add(Attribute("service.name", serviceName));
        foreach (var (key, value) in attrs)
        {
            resource.Attributes.Add(Attribute(key, value));
        }

        return resource;
    }

    public static Span Span(
        string traceId,
        string spanId,
        string operationName,
        long startNanos = 0,
        long endNanos = 1_000_000_000,
        (string Key, string Value)[]? strAttrs = null,
        (string Key, long Value)[]? intAttrs = null
    )
    {
        var span = new Span
        {
            TraceId = Google.Protobuf.ByteString.CopyFrom(Convert.FromHexString(traceId)),
            SpanId = Google.Protobuf.ByteString.CopyFrom(Convert.FromHexString(spanId)),
            Name = operationName,
            StartTimeUnixNano = (ulong)startNanos,
            EndTimeUnixNano = (ulong)endNanos,
        };
        span.Attributes.Add(Attribute("gen_ai.operation.name", operationName));

        if (strAttrs is not null)
        {
            foreach (var (key, value) in strAttrs)
            {
                span.Attributes.Add(Attribute(key, value));
            }
        }

        if (intAttrs is not null)
        {
            foreach (var (key, value) in intAttrs)
            {
                span.Attributes.Add(
                    new KeyValue
                    {
                        Key = key,
                        Value = new AnyValue { IntValue = value },
                    }
                );
            }
        }

        return span;
    }

    public static KeyValue Attribute(string key, string value) =>
        new()
        {
            Key = key,
            Value = new AnyValue { StringValue = value },
        };
}
