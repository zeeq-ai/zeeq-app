using Zeeq.Core.Identity;
using Zeeq.Core.Llm;
using Microsoft.AspNetCore.Authorization;

namespace Zeeq.Platform.Llm;

/// <summary>
/// Organization-scoped LLM settings and encrypted-key management endpoints.
/// </summary>
public sealed class LlmSettingsEndpoints : IEndpoint
{
    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder app, IEndpointRouteBuilder rootApp)
    {
        var group = app.MapGroup("orgs/{orgId}/llm-settings")
            .WithTags("LLM Settings")
            .RequireRouteOrganizationMatchesCookie()
            .RequireAuthorization(
                new AuthorizeAttribute
                {
                    AuthenticationSchemes = SetupIdentityExtension.CookieScheme,
                }
            );

        // GET /api/v1/orgs/{orgId}/llm-settings
        group
            .MapGet(
                "/",
                static (
                    string orgId,
                    ClaimsPrincipal user,
                    [FromServices] GetLlmSettingsHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, user, ct)
            )
            .WithName("GetOrganizationLlmSettings")
            .Produces<LlmSettingsViewResponse>()
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Get org LLM settings.")
            .WithDescription(
                """
                Returns the organization's LLM configuration for `orgId`. Every member sees the
                high-level routing state (whether the org is configured and usable); owners and
                admins additionally receive the full tier settings and API-key metadata.

                Key metadata never includes plaintext key material.
                """
            );

        // PUT /api/v1/orgs/{orgId}/llm-settings
        group
            .MapPut(
                "/",
                static (
                    string orgId,
                    SaveLlmSettingsRequest request,
                    ClaimsPrincipal user,
                    [FromServices] SaveLlmSettingsHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, request, user, ct)
            )
            .RequireActiveOrganization()
            .WithName("SaveOrganizationLlmSettings")
            .Produces<LlmSettingsViewResponse>()
            .Produces<LlmSettingsError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Save org LLM settings.")
            .WithDescription(
                """
                Updates the organization's LLM tier settings for `orgId` — the provider, model,
                and key selections that route Zeeq's Fast and other model tiers.

                Restricted to organization owners and admins (`403` otherwise).
                """
            );

        // POST /api/v1/orgs/{orgId}/llm-settings/keys
        group
            .MapPost(
                "/keys",
                static (
                    string orgId,
                    CreateLlmApiKeyRequest request,
                    ClaimsPrincipal user,
                    [FromServices] CreateLlmApiKeyHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, request, user, ct)
            )
            .RequireActiveOrganization()
            .WithName("CreateOrganizationLlmApiKey")
            .Produces<LlmApiKeyResponse>(StatusCodes.Status201Created)
            .Produces<LlmSettingsError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Add an LLM API key.")
            .WithDescription(
                """
                Stores a provider API key for the organization (`orgId`). The plaintext key is
                **write-only**: it is encrypted at rest and can never be read back through the
                API — subsequent responses expose only metadata.

                Returns `201 Created` with the new key's metadata. Restricted to organization
                owners and admins (`403` otherwise).
                """
            );

        // PATCH /api/v1/orgs/{orgId}/llm-settings/keys/{keyId}/name
        group
            .MapPatch(
                "/keys/{keyId}/name",
                static (
                    string orgId,
                    string keyId,
                    RenameLlmApiKeyRequest request,
                    ClaimsPrincipal user,
                    [FromServices] RenameLlmApiKeyHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, keyId, request, user, ct)
            )
            .RequireActiveOrganization()
            .WithName("RenameOrganizationLlmApiKey")
            .Produces<LlmApiKeyResponse>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Rename an LLM API key.")
            .WithDescription(
                """
                Updates the display name of an active stored key (`keyId`) for organization
                `orgId`. Only the label changes; the encrypted key material is untouched.

                Restricted to organization owners and admins (`403` otherwise).
                """
            );

        // PUT /api/v1/orgs/{orgId}/llm-settings/keys/{keyId}/rotate
        group
            .MapPut(
                "/keys/{keyId}/rotate",
                static (
                    string orgId,
                    string keyId,
                    RotateLlmApiKeyRequest request,
                    ClaimsPrincipal user,
                    [FromServices] RotateLlmApiKeyHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, keyId, request, user, ct)
            )
            .RequireActiveOrganization()
            .WithName("RotateOrganizationLlmApiKey")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<LlmSettingsError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Rotate an LLM API key.")
            .WithDescription(
                """
                Replaces the secret behind an existing key (`keyId`) for organization `orgId`
                with a new plaintext value, keeping the same key identity and references. Like
                creation, the supplied plaintext is write-only and encrypted at rest.

                Restricted to organization owners and admins (`403` otherwise).
                """
            );

        // DELETE /api/v1/orgs/{orgId}/llm-settings/keys/{keyId}
        group
            .MapDelete(
                "/keys/{keyId}",
                static (
                    string orgId,
                    string keyId,
                    ClaimsPrincipal user,
                    [FromServices] DeleteLlmApiKeyHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, keyId, user, ct)
            )
            .RequireActiveOrganization()
            .WithName("DeleteOrganizationLlmApiKey")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<LlmSettingsError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Delete an LLM API key.")
            .WithDescription(
                """
                Disables the stored key (`keyId`) for organization `orgId`. The key must not be
                referenced by the active tier settings; reassign those settings first, or the
                request is rejected (`400`).

                Restricted to organization owners and admins (`403` otherwise).
                """
            );

        // POST /api/v1/orgs/{orgId}/llm-settings/test
        group
            .MapPost(
                "/test",
                static (
                    string orgId,
                    TestLlmSettingsRequest request,
                    ClaimsPrincipal user,
                    [FromServices] TestLlmSettingsHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, request, user, ct)
            )
            .RequireActiveOrganization()
            .WithName("TestOrganizationLlmSettings")
            .Produces<LlmProviderAccessTestResult>()
            .Produces<LlmSettingsError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Test LLM provider access.")
            .WithDescription(
                """
                Runs a bounded, live access check against a provider/model/key combination for
                organization `orgId` and reports whether Zeeq can reach it. Useful for
                validating a key before saving it into the tier settings.

                Output is sanitized so provider errors never leak secrets. Restricted to
                organization owners and admins (`403` otherwise).
                """
            );
    }
}
