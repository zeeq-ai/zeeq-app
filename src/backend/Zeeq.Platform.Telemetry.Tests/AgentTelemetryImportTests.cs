using System.Security.Claims;
using System.Text.Json;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;
using Zeeq.Platform.Telemetry.Adapters;
using Zeeq.Platform.Telemetry.Adapters.ZeeqAgent;
using Zeeq.Platform.Telemetry.Filtering;
using Zeeq.Platform.Telemetry.Ingest.Import;
using Zeeq.Platform.Telemetry.Ingest.Otlp;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIddict.Abstractions;
using OpenTelemetry.Proto.Collector.Logs.V1;

namespace Zeeq.Platform.Telemetry.Tests;

/// <summary>
/// Direct JSON import tests for validation, raw-ingest scoping, and adapter normalization.
/// </summary>
[Category("Unit")]
public sealed class AgentTelemetryImportTests
{
    [Test]
    public async Task HandleAsync_InvalidRequest_ReturnsValidationProblem()
    {
        var handler = new AgentTelemetryImportHandler(
            new AgentTelemetryImportValidator(),
            new AgentTelemetryImportOtlpMapper(),
            CreateIngestService(new CapturingRawStore()),
            PrincipalAccessor()
        );

        var result = await handler.HandleAsync(
            new AgentTelemetryImportRequest("", "", []),
            CancellationToken.None
        );

        await Assert
            .That(((IStatusCodeHttpResult)result).StatusCode)
            .IsEqualTo(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task HandleAsync_UsesValidatedPrincipalScope()
    {
        var rawStore = new CapturingRawStore();
        var handler = new AgentTelemetryImportHandler(
            new AgentTelemetryImportValidator(),
            new AgentTelemetryImportOtlpMapper(),
            CreateIngestService(rawStore),
            PrincipalAccessor()
        );

        var result = await handler.HandleAsync(Request(), CancellationToken.None);

        await Assert
            .That(((IStatusCodeHttpResult)result).StatusCode)
            .IsEqualTo(StatusCodes.Status202Accepted);
        await Assert.That(rawStore.LastIngestUserId).IsEqualTo("validated-user");
        await Assert.That(rawStore.LastIngestOrganizationId).IsEqualTo("validated-org");
        await Assert.That(rawStore.LastMetadata!.Harness).IsEqualTo("zeeq-agent");
        await Assert.That(rawStore.LastMetadata.RecordCount).IsEqualTo(1);
    }

    [Test]
    public async Task MapperAndAdapter_PreserveHarnessRepositoryBranchAndEventFields()
    {
        var mapper = new AgentTelemetryImportOtlpMapper();
        var request = Request();
        var export = ExportLogsServiceRequest.Parser.ParseFrom(mapper.Map(request));
        var context = TelemetryLogRecordContext.Enumerate(export).Single();
        var adapter = new ZeeqAgentTelemetryAdapter();

        await Assert.That(adapter.CanHandle(context)).IsTrue();

        var result = adapter.Adapt(context);

        await Assert.That(result.Conversation.ConversationId).IsEqualTo("conversation-1");
        await Assert.That(result.Conversation.Harness).IsEqualTo("codex");
        await Assert.That(result.Conversation.AppVersion).IsEqualTo("1.2.3");
        await Assert
            .That(result.Conversation.RepoRemoteUrl)
            .IsEqualTo("https://github.com/acme/repo.git");
        await Assert.That(result.Conversation.HeadBranch).IsEqualTo("feat/import");
        await Assert.That(result.Event!.EventType).IsEqualTo(AgentSessionEventType.ToolResult);
        await Assert.That(result.Event.ToolName).IsEqualTo("mcp__zeeq__search_sections");
        await Assert.That(result.Event.ToolCallId).IsEqualTo("call-1");
        await Assert.That(result.Event.SourceRecordId).IsEqualTo("event-1");
        await Assert
            .That(result.Event.ArgumentsJson!.RootElement.GetProperty("query").GetString())
            .IsEqualTo("telemetry");
    }

    private static AgentTelemetryImportRequest Request() =>
        new(
            ConversationId: "conversation-1",
            HarnessName: "codex",
            HarnessVersion: "1.2.3",
            RepositoryRemoteUrl: "https://github.com/acme/repo.git",
            HeadBranch: "feat/import",
            HeadSha: "abc123",
            Events:
            [
                new(
                    Kind: AgentEventKind.ToolResult,
                    EventId: "event-1",
                    OccurredAtUtc: DateTimeOffset.Parse("2026-07-15T12:00:00Z"),
                    ToolName: "mcp__zeeq__search_sections",
                    ToolCallId: "call-1",
                    McpServerName: "zeeq",
                    ToolArguments: JsonDocument.Parse("""{"query":"telemetry"}""").RootElement,
                    ToolResult: "ok",
                    ToolDurationMs: 42,
                    ToolSucceeded: true,
                    Model: "gpt-5",
                    UserEmail: "payload@example.com",
                    OrganizationId: "payload-org"
                ),
            ]
        );

    private static IHttpContextAccessor PrincipalAccessor() =>
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
        };

    private static OtlpLogIngestService CreateIngestService(CapturingRawStore rawStore) =>
        new(
            rawStore,
            new AgentTelemetryLogFilter([new ZeeqAgentTelemetryAdapter()]),
            new AgentTelemetrySpanFilter([]),
            new TelemetryRawLogMetadataExtractor(),
            NullLogger<OtlpLogIngestService>.Instance
        );

    private sealed class CapturingRawStore : ITelemetryRawRequestStore
    {
        public string? LastIngestUserId { get; private set; }
        public string? LastIngestOrganizationId { get; private set; }
        public TelemetryRawRequestMetadata? LastMetadata { get; private set; }

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
        ) => Task.FromResult("raw-trace");

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
