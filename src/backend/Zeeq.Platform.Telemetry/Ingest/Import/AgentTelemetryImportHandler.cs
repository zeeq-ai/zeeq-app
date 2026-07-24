using System.Security.Claims;
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
    IZeeqIdentityStore identityStore,
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

        var principal = httpContextAccessor.HttpContext!.User;
        var identity = principal.AsZeeqIdentity();
        var authenticatedEmail = await ResolveAuthenticatedEmailAsync(
            principal,
            identity,
            cancellationToken
        );

        var accepted = await ingestService.StoreLogsAsync(
            mapper.Map(WithAuthenticatedUserEmail(request, authenticatedEmail)),
            identity.OwnerUserId,
            identity.OrganizationId,
            cancellationToken
        );

        return Results.Accepted(value: new AgentTelemetryImportResponse(accepted));
    }

    /// <summary>
    /// Resolves the ingest principal's email from the token's own <c>email</c> claim,
    /// falling back to a user-store lookup by id when the token doesn't carry one
    /// (e.g. some machine-credential flows omit it).
    /// </summary>
    /// <remarks>
    /// NOTE: <paramref name="identity"/>.OwnerUserId is the OpenIddict <c>sub</c> claim,
    /// which for client-credentials tokens is the OAuth client id, not the human owner
    /// (see <c>AuthClaims.OwnerUserId</c> vs. <c>Claims.Subject</c> in
    /// <c>ClientCredentialOpenIddictFactory</c>). The DB fallback below is therefore only
    /// correct for user-owned tokens (the only credential type this endpoint sees today).
    /// Revisit if client-credentials tokens are ever used to call this endpoint directly.
    /// </remarks>
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

    /// <summary>
    /// Overwrites <see cref="ImportedAgentEvent.UserEmail"/> on every event with the
    /// authenticated ingest principal's own email, ignoring whatever the client reported.
    /// </summary>
    /// <remarks>
    /// This endpoint always requires authentication, and the token is issued by this
    /// system's own OpenIddict server — the caller's email is never something the client
    /// needs to (or should be trusted to) tell us. <c>CreatedById</c> is already always
    /// sourced from this same principal (never the request body); overriding here keeps
    /// <c>OwnerEmail</c> consistent with it instead of letting a client mislabel a
    /// conversation with an arbitrary email. Applied unconditionally — including when
    /// <paramref name="authenticatedEmail"/> is <see langword="null"/> — so a client value
    /// is never left in place just because the token happened to lack an email claim.
    /// </remarks>
    private static AgentTelemetryImportRequest WithAuthenticatedUserEmail(
        AgentTelemetryImportRequest request,
        string? authenticatedEmail
    ) =>
        request with
        {
            Events =
            [
                .. request.Events.Select(evt => evt with { UserEmail = authenticatedEmail }),
            ],
        };
}
