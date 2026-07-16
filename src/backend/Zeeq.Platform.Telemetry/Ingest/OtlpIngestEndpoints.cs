using Zeeq.Core.Common.AspNetCore.Contracts;
using Zeeq.Platform.Telemetry.Ingest.Import;
using Zeeq.Platform.Telemetry.Ingest.Otlp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Zeeq.Platform.Telemetry.Ingest;

/// <summary>
/// Routes for OTLP/HTTP telemetry receivers and direct JSON telemetry import.
/// </summary>
/// <remarks>
/// Flow: collector sends protobuf payloads to <c>/v1/logs</c> or <c>/v1/traces</c>
/// (mapped on <c>rootApp</c> to bypass the <c>/api/v1</c> prefix). Each route
/// delegates to an <c>IEndpointHandler</c> — <c>OtlpHttpLogReceiver</c>
/// or <c>OtlpHttpTraceReceiver</c> — which extracts the validated JWT
/// principal, then calls <c>OtlpLogIngestService</c> to prune, filter, and
/// persist raw protobuf to the <c>UNLOGGED</c> <c>telemetry_raw_requests</c> table
/// via <c>ITelemetryRawRequestStore</c>.
///
/// After persistence, asynchronous processing picks up raw rows through
/// <c>TelemetryProcessingService</c> (cluster-leased batches), dispatches to
/// harness-specific adapters (Claude Code / Codex / Copilot Chat), normalizes
/// into <c>AgentConversation</c> and <c>AgentSessionEvent</c> domain
/// rows, then deletes the raw row. The domain tables are range-partitioned by
/// <c>occurred_at_utc</c> via <c>pg_partman</c>.
///
/// OTLP routes are excluded from OpenAPI because they are spec-defined
/// machine-to-machine protocol endpoints. The JSON import route is a documented
/// product API under <c>/api/v1</c>.
/// </remarks>
public sealed class OtlpIngestEndpoints : IEndpoint
{
    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder app, IEndpointRouteBuilder rootApp)
    {
        // POST /v1/logs
        rootApp
            .MapPost(
                "/v1/logs",
                static (
                    [FromServices] OtlpHttpLogReceiver handler,
                    HttpRequest request,
                    CancellationToken cancellationToken
                ) => handler.HandleAsync(request, cancellationToken)
            )
            .RequireAuthorization()
            .WithName("OtlpHttpLogsExport")
            .ExcludeFromDescription();

        // POST /v1/traces
        rootApp
            .MapPost(
                "/v1/traces",
                static (
                    [FromServices] OtlpHttpTraceReceiver handler,
                    HttpRequest request,
                    CancellationToken cancellationToken
                ) => handler.HandleAsync(request, cancellationToken)
            )
            .RequireAuthorization()
            .WithName("OtlpHttpTracesExport")
            .ExcludeFromDescription();

        // POST /api/v1/telemetry/import
        app.MapGroup("telemetry")
            .MapPost(
                "import",
                static (
                    [FromServices] AgentTelemetryImportHandler handler,
                    AgentTelemetryImportRequest request,
                    CancellationToken cancellationToken
                ) => handler.HandleAsync(request, cancellationToken)
            )
            .WithName("AgentTelemetryImport")
            .WithTags("Telemetry")
            .WithSummary("Import agent telemetry.")
            .WithDescription(
                """
                Accepts first-party JSON agent telemetry and sends it through the
                same raw ingest, filtering, and processing path used by OTLP logs.
                The authenticated principal determines user and organization scope.
                """
            )
            .Produces<AgentTelemetryImportResponse>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem();
    }
}
