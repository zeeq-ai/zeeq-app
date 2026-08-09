using Zeeq.Core.Documents;
using Zeeq.Core.Identity;
using Zeeq.Core.Llm;
using Zeeq.Core.Models;
using Zeeq.Integrations.Notion;

namespace Zeeq.Platform.Documents;

/// <summary>Returns authenticated setup state for a Notion-backed library webhook.</summary>
public sealed class GetNotionWebhookStateHandler(
    ILibraryDocumentStore libraries,
    IEncryptedValueStore encryptedValues,
    EncryptedValueEncryptionService encryption,
    NotionCallbackTokenProtector callbackTokens
) : IEndpointHandler
{
    /// <summary>Handles the Notion webhook state request.</summary>
    public async Task<
        Results<Ok<NotionWebhookStateResponse>, BadRequest<LibraryError>, NotFound>
    > HandleAsync(
        string orgId,
        string name,
        HttpRequest request,
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(orgId))
        {
            return TypedResults.BadRequest(new LibraryError("Active organization is required."));
        }

        var library = await libraries.GetLibraryAsync(orgId, name, ct);
        if (library is null)
        {
            return TypedResults.NotFound();
        }

        var notion = library.ExternalSource?.Notion;
        if (library.SourceKind != RepositorySourceKind.Notion.ToString() || notion is null)
        {
            return TypedResults.BadRequest(
                new LibraryError("This library is not backed by Notion.")
            );
        }

        var verificationToken = await DecryptVerificationTokenAsync(orgId, notion, ct);

        return TypedResults.Ok(
            NotionWebhookEndpointSupport.ToStateResponse(
                request,
                callbackTokens,
                library,
                notion,
                verificationToken
            )
        );
    }

    private async Task<string?> DecryptVerificationTokenAsync(
        string organizationId,
        NotionSourceConfiguration notion,
        CancellationToken cancellationToken
    )
    {
        if (notion.VerificationTokenValueId is not { } verificationTokenValueId)
        {
            return null;
        }

        var encryptedToken = await encryptedValues.FindActiveAsync(
            organizationId,
            verificationTokenValueId,
            cancellationToken
        );

        return encryptedToken is null
            ? null
            : await encryption.DecryptAsync(
                encryptedToken,
                EncryptedValueKind.SecretString,
                cancellationToken
            );
    }
}

/// <summary>Resets Notion webhook setup state for a Notion-backed library.</summary>
public sealed class ResetNotionWebhookStateHandler(
    ILibraryDocumentStore libraries,
    INotionWebhookStore webhookState,
    NotionCallbackTokenProtector callbackTokens
) : IEndpointHandler
{
    /// <summary>Handles the Notion webhook reset request.</summary>
    public async Task<
        Results<Ok<NotionWebhookStateResponse>, BadRequest<LibraryError>, NotFound>
    > HandleAsync(
        string orgId,
        string name,
        HttpRequest request,
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(orgId))
        {
            return TypedResults.BadRequest(new LibraryError("Active organization is required."));
        }

        var library = await libraries.GetLibraryAsync(orgId, name, ct);
        if (library is null)
        {
            return TypedResults.NotFound();
        }

        var notion = library.ExternalSource?.Notion;
        if (library.SourceKind != RepositorySourceKind.Notion.ToString() || notion is null)
        {
            return TypedResults.BadRequest(
                new LibraryError("This library is not backed by Notion.")
            );
        }

        var result = await webhookState.ResetAsync(orgId, library.Id, DateTimeOffset.UtcNow, ct);
        if (result == NotionWebhookResetResult.NotFound)
        {
            return TypedResults.NotFound();
        }

        var resetNotion = notion with
        {
            VerificationTokenValueId = null,
            WebhookSubscriptionId = null,
            WebhookActivatedAtUtc = null,
            CallbackTokenSerial = notion.CallbackTokenSerial + 1,
        };

        return TypedResults.Ok(
            NotionWebhookEndpointSupport.ToStateResponse(
                request,
                callbackTokens,
                library,
                resetNotion,
                verificationToken: null
            )
        );
    }
}

internal static class NotionWebhookEndpointSupport
{
    public static NotionWebhookStateResponse ToStateResponse(
        HttpRequest request,
        NotionCallbackTokenProtector callbackTokens,
        Library library,
        NotionSourceConfiguration notion,
        string? verificationToken
    )
    {
        var callbackToken = callbackTokens.Protect(
            new NotionCallbackPayload(
                library.OrganizationId,
                library.Id,
                notion.CallbackTokenSerial
            )
        );

        return new NotionWebhookStateResponse(
            BuildCallbackUrl(request, callbackToken),
            notion.CallbackTokenSerial,
            verificationToken,
            VerificationTokenAvailable: verificationToken is not null,
            WebhookActivated: notion.WebhookActivatedAtUtc is not null,
            notion.WebhookActivatedAtUtc,
            notion.WebhookSubscriptionId
        );
    }

    private static string BuildCallbackUrl(HttpRequest request, string callbackToken) =>
        $"{request.Scheme}://{request.Host}{request.PathBase.ToUriComponent()}/api/v1/integrations/notion/webhook/{Uri.EscapeDataString(callbackToken)}";
}
