using Zeeq.Core.Common.AspNetCore.Contracts;
using Zeeq.Core.Identity;
using Zeeq.Platform.Telemetry.Ingest.Otlp;
using Microsoft.AspNetCore.Http;

namespace Zeeq.Platform.Telemetry.Ingest.Import;

/// <summary>Accepts validated direct-import JSON and routes it through the OTLP log ingest path.</summary>
public sealed class AgentTelemetryImportHandler(
    AgentTelemetryImportValidator validator,
    AgentTelemetryImportOtlpMapper mapper,
    OtlpLogIngestService ingestService,
    IHttpContextAccessor httpContextAccessor
) : IEndpointHandler
{
    /// <summary>
    /// Validates and accepts direct agent telemetry for asynchronous processing.
    /// </summary>
    /// <param name="request">Direct-import request body.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <c>202 Accepted</c> when the shared ingest path stores one or more events;
    /// <c>400 Bad Request</c> when the request contract is invalid.
    /// </returns>
    public async Task<IResult> HandleAsync(
        AgentTelemetryImportRequest request,
        CancellationToken cancellationToken
    )
    {
        var validationErrors = validator.Validate(request);
        if (validationErrors.Count > 0)
        {
            return Results.ValidationProblem(validationErrors);
        }

        var identity = httpContextAccessor.HttpContext!.User.AsZeeqIdentity();

        var accepted = await ingestService.StoreLogsAsync(
            mapper.Map(request),
            identity.OwnerUserId,
            identity.OrganizationId,
            cancellationToken
        );

        return Results.Accepted(value: new AgentTelemetryImportResponse(accepted));
    }
}
