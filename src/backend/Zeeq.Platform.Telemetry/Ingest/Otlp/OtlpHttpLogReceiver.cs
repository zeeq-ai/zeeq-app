using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Zeeq.Core.Common.AspNetCore.Contracts;
using Zeeq.Core.Identity;

namespace Zeeq.Platform.Telemetry.Ingest.Otlp;

/// <summary>
/// OTLP/HTTP logs receiver. Parses the raw protobuf body, extracts the ingest
/// principal from the validated bearer token (forwarded by the collector's
/// headers_setter), and sends it to the shared ingest path.
/// </summary>
/// <remarks>
/// Gzip decompression is automatic via <c>RequestDecompressionMiddleware</c> —
/// <c>request.Body</c> is already decompressed here. Returns success only after raw
/// persistence; queue or storage saturation is retryable.
/// </remarks>
public sealed class OtlpHttpLogReceiver(
    OtlpLogIngestService ingestService,
    IZeeqIdentityStore identityStore,
    IHttpContextAccessor httpContextAccessor
) : IEndpointHandler
{
    /// <summary>
    /// Handles an OTLP/HTTP logs export request.
    /// </summary>
    /// <param name="request">The HTTP request with protobuf body.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <c>200 OK</c> on success (raw payload persisted). Errors from the
    /// underlying store are surfaced as 5xx responses so the collector can retry.
    /// </returns>
    public async Task<IResult> HandleAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();

        // TODO: Avoid double allocation — parse directly from the MemoryStream
        // buffer or use ArrayPool<byte> to eliminate the ToArray() copy.
        await request.Body.CopyToAsync(buffer, cancellationToken);

        var identity = httpContextAccessor.HttpContext!.User.AsZeeqIdentity();
        var authenticatedEmail = await ResolveAuthenticatedEmailAsync(
            httpContextAccessor.HttpContext.User,
            identity,
            cancellationToken
        );

        await ingestService.StoreLogsAsync(
            buffer.ToArray(),
            identity.OwnerUserId,
            identity.OrganizationId,
            authenticatedEmail,
            cancellationToken
        );

        return Results.Ok();
    }

    private async Task<string?> ResolveAuthenticatedEmailAsync(
        ClaimsPrincipal principal,
        ZeeqIdentity identity,
        CancellationToken cancellationToken
    )
    {
        var claimEmail = principal.AuthenticatedUser()?.Email;
        if (!string.IsNullOrWhiteSpace(claimEmail))
        {
            return claimEmail;
        }

        return string.IsNullOrWhiteSpace(identity.OwnerUserId)
            ? null
            : await identityStore.FindUserEmailAsync(identity.OwnerUserId, cancellationToken);
    }
}
