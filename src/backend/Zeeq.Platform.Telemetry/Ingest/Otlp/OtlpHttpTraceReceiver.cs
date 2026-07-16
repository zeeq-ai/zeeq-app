using Zeeq.Core.Common.AspNetCore.Contracts;
using Zeeq.Core.Identity;
using Microsoft.AspNetCore.Http;

namespace Zeeq.Platform.Telemetry.Ingest.Otlp;

/// <summary>
/// OTLP/HTTP traces receiver. Identical structure to <see cref="OtlpHttpLogReceiver"/>,
/// discriminated via <c>OtlpSignalType.Traces</c>.
/// </summary>
public sealed class OtlpHttpTraceReceiver(
    OtlpLogIngestService ingestService,
    IHttpContextAccessor httpContextAccessor
) : IEndpointHandler
{
    /// <summary>
    /// Handles an OTLP/HTTP traces export request.
    /// </summary>
    /// <inheritdoc cref="OtlpHttpLogReceiver.HandleAsync"/>
    public async Task<IResult> HandleAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();

        // TODO: Avoid double allocation — parse directly from the MemoryStream
        // buffer or use ArrayPool<byte> to eliminate the ToArray() copy.
        await request.Body.CopyToAsync(buffer, cancellationToken);

        var identity = httpContextAccessor.HttpContext!.User.AsZeeqIdentity();

        await ingestService.StoreTracesAsync(
            buffer.ToArray(),
            identity.OwnerUserId,
            identity.OrganizationId,
            cancellationToken
        );

        return Results.Ok();
    }
}
