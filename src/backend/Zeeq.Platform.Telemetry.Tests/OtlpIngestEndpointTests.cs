using System.Diagnostics;
using System.Security.Claims;
using Zeeq.Core.Common;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;
using Zeeq.Platform.Telemetry.Adapters.ClaudeCode;
using Zeeq.Platform.Telemetry.Adapters.Copilot;
using Zeeq.Platform.Telemetry.Filtering;
using Zeeq.Platform.Telemetry.Ingest;
using Zeeq.Platform.Telemetry.Ingest.Otlp;
using Google.Protobuf;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIddict.Abstractions;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OtlpLog = OpenTelemetry.Proto.Logs.V1;

namespace Zeeq.Platform.Telemetry.Tests;

/// <summary>
/// Endpoint and shared-ingest tests that verify authenticated scope is copied to raw storage.
/// </summary>
[Category("Unit")]
public sealed class OtlpIngestEndpointTests
{
    [Test]
    public async Task MapEndpoints_LogsAndTracesRequireDefaultAuthorization()
    {
        var builder = WebApplication.CreateBuilder();
        await using var app = builder.Build();
        var endpoints = new OtlpIngestEndpoints();

        endpoints.MapEndpoints(app, app);

        var mapped = ((IEndpointRouteBuilder)app)
            .DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText is "/v1/logs" or "/v1/traces")
            .ToList();

        await Assert.That(mapped).Count().IsEqualTo(2);
        foreach (var endpoint in mapped)
        {
            var authorization = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
            await Assert
                .That(authorization.Any(data => data.Policy is null))
                .IsTrue();
        }
    }

    [Test]
    public async Task MapEndpoints_RestImport_IsDocumentedApiRoute()
    {
        var builder = WebApplication.CreateBuilder();
        await using var app = builder.Build();
        var endpoints = new OtlpIngestEndpoints();

        endpoints.MapEndpoints(app, app);

        var endpoint = ((IEndpointRouteBuilder)app)
            .DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(endpoint => endpoint.RoutePattern.RawText == "telemetry/import");

        await Assert
            .That(endpoint.Metadata.GetMetadata<IExcludeFromDescriptionMetadata>())
            .IsNull();
        await Assert
            .That(endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()!.EndpointName)
            .IsEqualTo("AgentTelemetryImport");
    }

    [Test]
    public async Task HandleAsync_ValidatedPrincipal_StoresOnlyPrincipalScope()
    {
        var rawStore = new CapturingRawStore();
        var receiver = new OtlpHttpLogReceiver(
            CreateIngestService(rawStore),
            new HttpContextAccessor
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(
                        new ClaimsIdentity(
                            [
                                new(OpenIddictConstants.Claims.Subject, "validated-user"),
                                new(AuthClaims.OrganizationId, "validated-org"),
                            ],
                            authenticationType: "test"
                        )
                    ),
                },
            }
        );
        var export = LogsRequest("payload-session");
        var requestContext = new DefaultHttpContext();
        requestContext.Request.Body = new MemoryStream(export.ToByteArray());

        var result = await receiver.HandleAsync(requestContext.Request, CancellationToken.None);

        await Assert
            .That(((IStatusCodeHttpResult)result).StatusCode)
            .IsEqualTo(StatusCodes.Status200OK);
        await Assert.That(rawStore.LastIngestUserId).IsEqualTo("validated-user");
        await Assert.That(rawStore.LastIngestOrganizationId).IsEqualTo("validated-org");
        await Assert.That(rawStore.LastMetadata!.RecordCount).IsEqualTo(1);
    }

    [Test]
    public async Task StoreTracesAsync_RejectedSpans_DoesNotCallRawStore()
    {
        var rawStore = new CapturingRawStore();
        var service = CreateIngestService(rawStore);
        var request = new ExportTraceServiceRequest();

        var stored = await service.StoreTracesAsync(
            request.ToByteArray(),
            ingestUserId: "user",
            ingestOrganizationId: "org",
            CancellationToken.None
        );

        await Assert.That(stored).IsEqualTo(0);
        await Assert.That(rawStore.StoreTracesCalls).IsEqualTo(0);
    }

    [Test]
    public async Task StoreLogsAsync_AcceptedPayload_EmitsIngestActivity()
    {
        Activity? captured = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ZeeqTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllData,
            ActivityStarted = activity =>
            {
                if (activity.OperationName == "telemetry.ingest.logs")
                {
                    captured = activity;
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        var service = CreateIngestService(new CapturingRawStore());

        await service.StoreLogsAsync(
            LogsRequest("activity-session").ToByteArray(),
            ingestUserId: "user",
            ingestOrganizationId: "org",
            CancellationToken.None
        );

        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.GetTagItem("telemetry.signal_type")).IsEqualTo("logs");
        await Assert.That(captured.GetTagItem("telemetry.record_count")).IsEqualTo(1);
    }

    private static OtlpLogIngestService CreateIngestService(CapturingRawStore rawStore) =>
        new(
            rawStore,
            new AgentTelemetryLogFilter([new ClaudeCodeTelemetryAdapter()]),
            new AgentTelemetrySpanFilter([new CopilotChatTelemetryAdapter()]),
            new TelemetryRawLogMetadataExtractor(),
            NullLogger<OtlpLogIngestService>.Instance
        );

    private static ExportLogsServiceRequest LogsRequest(string sessionId)
    {
        var request = new ExportLogsServiceRequest();
        var resourceLogs = new OtlpLog.ResourceLogs
        {
            Resource = TestTelemetry.Resource("claude-code"),
        };
        var scopeLogs = new OtlpLog.ScopeLogs();
        var record = new OtlpLog.LogRecord
        {
            Body = new() { StringValue = "claude_code.user_prompt" },
        };
        record.Attributes.Add(TestTelemetry.Attribute("session.id", sessionId));
        scopeLogs.LogRecords.Add(record);
        resourceLogs.ScopeLogs.Add(scopeLogs);
        request.ResourceLogs.Add(resourceLogs);
        return request;
    }

    private sealed class CapturingRawStore : ITelemetryRawRequestStore
    {
        public string? LastIngestUserId { get; private set; }
        public string? LastIngestOrganizationId { get; private set; }
        public TelemetryRawRequestMetadata? LastMetadata { get; private set; }
        public int StoreTracesCalls { get; private set; }

        public Task<string> StoreLogsAsync(
            byte[] payload,
            TelemetrySignalType signalType,
            TelemetryRawRequestMetadata metadata,
            string ingestUserId,
            string ingestOrganizationId,
            CancellationToken cancellationToken = default
        )
        {
            LastIngestUserId = ingestUserId;
            LastIngestOrganizationId = ingestOrganizationId;
            LastMetadata = metadata;
            return Task.FromResult("raw-log");
        }

        public Task<string> StoreTracesAsync(
            byte[] payload,
            TelemetrySignalType signalType,
            TelemetryRawRequestMetadata metadata,
            string ingestUserId,
            string ingestOrganizationId,
            CancellationToken cancellationToken = default
        )
        {
            StoreTracesCalls++;
            return Task.FromResult("raw-trace");
        }

        public Task<IReadOnlyList<TelemetryRawRequest>> ClaimBatchAsync(
            int batchSize,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IReadOnlyList<TelemetryRawRequest>>([]);

        public Task<bool> DeleteClaimedAsync(
            string rawRequestId,
            string processingLeaseId,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(false);

        public Task<int> ReleaseExpiredLeasesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task QuarantineAsync(
            string rawRequestId,
            string processingLeaseId,
            string reason,
            CancellationToken cancellationToken = default
        ) => Task.CompletedTask;

        public Task RecordProcessingFailureAsync(
            string rawRequestId,
            string processingLeaseId,
            Exception error,
            CancellationToken cancellationToken = default
        ) => Task.CompletedTask;
    }
}
