using System.Data;
using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Zeeq.Core.Common;
using Zeeq.Core.Llm;

namespace Zeeq.Runtime.Server.Setup;

/// <summary>
/// Setup class to add OpenTelemetry tracing, metrics, and logging to the service
/// collection for observability of the call paths.
/// </summary>
internal static class SetupTelemetryExtension
{
    private static readonly IEnumerable<KeyValuePair<string, object>> DefaultAttributes =
        new Dictionary<string, object>
        {
            ["service"] = "zeeq",
            ["service.version"] = GitVersionInfo.TelemetryVersion,
        };

    extension(IServiceCollection services)
    {
        /// <summary>
        /// Adds OpenTelemetry tracing, metrics, and logging to the service collection.
        /// </summary>
        /// <returns>The service collection with telemetry added.</returns>
        public IServiceCollection AddZeeqTelemetry()
        {
            ConfigureTracePropagation();

            services
                .AddOpenTelemetry()
                .ConfigureResource(r => r.AddService("zeeq").AddAttributes(DefaultAttributes))
                .WithTracing(b =>
                {
                    b.AddSource(
                            ZeeqTelemetry.Tracer.Name,
                            ZeeqTelemetry.ActivitySourceName,
                            LlmTelemetry.ActivitySourceName,
                            "Paramore.Brighter",
                            "System.Net.Http",
                            "Private.InternalDiagnostics.System.Net.Http"
                        )
                        .SetSampler(new ZeeqTraceSampler())
                        .AddAspNetCoreInstrumentation(config =>
                        {
                            config.RecordException = true;
                        })
                        .AddHttpClientInstrumentation()
                        .AddEntityFrameworkCoreInstrumentation(config =>
                        {
                            config.Filter = (_, _) => Activity.Current is not null;
                            config.EnrichWithIDbCommand = EnrichEfCommandActivity;
                        })
                        .ConfigureResource(r =>
                            r.AddService("zeeq").AddAttributes(DefaultAttributes)
                        );
                })
                .WithMetrics(b =>
                    b.AddMeter("*").AddAspNetCoreInstrumentation().AddHttpClientInstrumentation()
                )
                .WithLogging()
                .UseOtlpExporter();

            return services;
        }
    }

    /// <summary>
    /// Configures process-wide trace context propagation for HTTP and message queue boundaries.
    /// </summary>
    /// <remarks>
    /// Brighter writes queue trace headers through OpenTelemetry's global text-map propagator
    /// when it serializes a message for PostgreSQL. Set W3C trace context explicitly here so
    /// queue consumers can continue the publishing request trace instead of starting isolated
    /// spans when they later read the row.
    /// </remarks>
    private static void ConfigureTracePropagation()
    {
        Sdk.SetDefaultTextMapPropagator(
            new CompositeTextMapPropagator([new TraceContextPropagator(), new BaggagePropagator()])
        );
    }

    /// <summary>
    /// Promotes Zeeq EF query tags from SQL comments into span identity and attributes.
    /// </summary>
    /// <remarks>
    /// <c>TagWithCallSite</c> writes call-site metadata into the generated SQL as comments. Our
    /// <c>TagWithOperationCallSite</c> extension adds a second comment with a stable
    /// semantic operation name and caller member. EF instrumentation sees the final
    /// <see cref="IDbCommand" />, so this is the last reliable point to turn those
    /// comments into useful telemetry.
    ///
    /// Only Zeeq-tagged queries are renamed. Untagged EF spans keep the instrumentation
    /// default so framework or third-party queries are not mislabeled as application work.
    /// </remarks>
    private static void EnrichEfCommandActivity(Activity activity, IDbCommand command)
    {
        var metadata = EfCommandTagMetadata.Parse(command.CommandText);
        if (metadata.OperationName is null)
        {
            return;
        }

        activity.DisplayName = $"SQL: {metadata.OperationName}";
        activity.SetTag("db.operation.name", metadata.OperationName);
        activity.SetTag("zeeq.db.operation", metadata.OperationName);

        if (metadata.MemberName is not null)
        {
            activity.SetTag("code.function", metadata.MemberName);
            activity.SetTag("zeeq.db.member", metadata.MemberName);
        }

        if (metadata.FilePath is not null)
        {
            activity.SetTag("code.file.path", metadata.FilePath);
        }

        if (metadata.LineNumber is not null)
        {
            activity.SetTag("code.line.number", metadata.LineNumber);
        }

        if (metadata is { FilePath: not null, LineNumber: not null })
        {
            activity.SetTag("zeeq.db.call_site", $"{metadata.FilePath}:{metadata.LineNumber}");
        }
    }

    /// <summary>
    /// Parsed operation metadata carried by EF query-tag comments.
    /// </summary>
    /// <remarks>
    /// The SQL comment format is intentionally treated as an integration boundary with EF:
    /// Zeeq owns the <c>Zeeq.Query</c> line, while EF owns the call-site line emitted by
    /// <c>TagWithCallSite</c>. Keep the parser permissive so minor EF formatting changes do
    /// not break the operation name promotion that makes Postgres spans readable in Aspire.
    /// </remarks>
    private readonly record struct EfCommandTagMetadata(
        string? OperationName,
        string? MemberName,
        string? FilePath,
        int? LineNumber
    )
    {
        private const string CommentPrefix = "--";
        private const string ZeeqQueryPrefix = "Zeeq.Query:";
        private const string MemberPrefix = "Member:";
        private const string FilePrefix = "File:";

        /// <summary>
        /// Reads EF query-tag comments from a command text block.
        /// </summary>
        public static EfCommandTagMetadata Parse(string? commandText)
        {
            if (string.IsNullOrWhiteSpace(commandText))
            {
                return default;
            }

            string? operationName = null;
            string? memberName = null;
            string? filePath = null;
            int? lineNumber = null;

            using var reader = new StringReader(commandText);
            while (reader.ReadLine() is { } rawLine)
            {
                var line = rawLine.Trim();
                if (!line.StartsWith(CommentPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var comment = line[CommentPrefix.Length..].Trim();
                if (comment.StartsWith(ZeeqQueryPrefix, StringComparison.Ordinal))
                {
                    (operationName, memberName) = ParseZeeqQueryComment(comment);
                    continue;
                }

                (filePath, lineNumber) = ParseCallSiteComment(comment, filePath, lineNumber);
            }

            return new EfCommandTagMetadata(operationName, memberName, filePath, lineNumber);
        }

        /// <summary>
        /// Parses the comment produced by <c>TagWithOperationCallSite</c>.
        /// </summary>
        private static (string? OperationName, string? MemberName) ParseZeeqQueryComment(
            string comment
        )
        {
            var body = comment[ZeeqQueryPrefix.Length..].Trim();
            var semicolonIndex = body.IndexOf(';', StringComparison.Ordinal);
            if (semicolonIndex < 0)
            {
                return (NormalizeTagValue(body), null);
            }

            var operationName = NormalizeTagValue(body[..semicolonIndex]);
            var memberSection = body[(semicolonIndex + 1)..].Trim();
            var memberName = memberSection.StartsWith(MemberPrefix, StringComparison.Ordinal)
                ? NormalizeTagValue(memberSection[MemberPrefix.Length..])
                : null;

            return (operationName, memberName);
        }

        /// <summary>
        /// Parses EF call-site comments, accepting either <c>File: path:line</c> or
        /// bare <c>path:line</c> shapes.
        /// </summary>
        private static (string? FilePath, int? LineNumber) ParseCallSiteComment(
            string comment,
            string? existingFilePath,
            int? existingLineNumber
        )
        {
            var callSite = comment.StartsWith(FilePrefix, StringComparison.Ordinal)
                ? comment[FilePrefix.Length..].Trim()
                : comment;

            var separatorIndex = callSite.LastIndexOf(':');
            if (
                separatorIndex <= 0
                || separatorIndex == callSite.Length - 1
                || !int.TryParse(callSite[(separatorIndex + 1)..], out var parsedLine)
            )
            {
                return (existingFilePath, existingLineNumber);
            }

            var parsedFilePath = NormalizeTagValue(callSite[..separatorIndex]);
            return (parsedFilePath ?? existingFilePath, parsedLine);
        }

        /// <summary>
        /// Normalizes comment values and converts empty values to <c>null</c>.
        /// </summary>
        private static string? NormalizeTagValue(string value)
        {
            var trimmed = value.Trim();
            return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
        }
    }

    /// <summary>
    /// Samples application traces while dropping high-volume infrastructure-only spans.
    /// </summary>
    /// <remarks>
    /// This keeps Aspire focused on meaningful application operations instead of standalone
    /// outbound calls or Brighter's empty queue poll loop.
    ///
    /// Brighter creates a root <c>{routingKey} receive</c> span before each Postgres poll and
    /// enriches it only if a message is found. Sampling happens before enrichment, so we cannot
    /// distinguish empty polls from successful receives at this point. Dropping root receive
    /// spans removes the timer-driven poll noise and its child Npgsql select spans; publish,
    /// process, handler, ack/delete, and dead-letter spans still record the real message flow.
    ///
    /// Provider-level Npgsql tracing is intentionally not registered. It captures every Brighter
    /// queue insert, poll, acknowledgement, and delete, which makes webhook traces difficult to
    /// read. EF Core instrumentation remains enabled so DbContext queries issued by application
    /// code still appear in the trace without the queue transport query spam.
    ///
    /// If a future investigation cannot find an expected standalone DB, HTTP, or Brighter receive
    /// span, check this sampler first: those infrastructure roots are intentionally dropped.
    /// </remarks>
    private sealed class ZeeqTraceSampler : Sampler
    {
        private static readonly SamplingResult Drop = new(SamplingDecision.Drop);
        private static readonly SamplingResult RecordAndSample = new(
            SamplingDecision.RecordAndSample
        );

        public override SamplingResult ShouldSample(in SamplingParameters samplingParameters)
        {
            if (HasParent(samplingParameters.ParentContext))
            {
                return IsRecorded(samplingParameters.ParentContext) ? RecordAndSample : Drop;
            }

            if (
                samplingParameters.Kind == ActivityKind.Consumer
                && samplingParameters.Name.EndsWith(" receive", StringComparison.Ordinal)
            )
            {
                return Drop;
            }

            if (samplingParameters.Kind == ActivityKind.Client)
            {
                return Drop;
            }

            return RecordAndSample;
        }

        private static bool HasParent(ActivityContext parentContext) =>
            parentContext.TraceId != default && parentContext.SpanId != default;

        private static bool IsRecorded(ActivityContext parentContext) =>
            parentContext.TraceFlags.HasFlag(ActivityTraceFlags.Recorded);
    }
}
