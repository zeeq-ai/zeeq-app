using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Zeeq.Integrations.Notion;

/// <summary>Protects the tenant context carried by a long-lived Notion callback URL.</summary>
public sealed class NotionCallbackTokenProtector(IDataProtectionProvider dataProtectionProvider)
{
    private const string Purpose = "Zeeq.Notion.Callback.v1";
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector(Purpose);

    /// <summary>Creates an opaque URL-safe callback token.</summary>
    public string Protect(NotionCallbackPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ValidatePayload(payload);

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(
            payload,
            NotionWebhookJsonContext.Default.NotionCallbackPayload
        );
        return Base64Url.EncodeToString(_protector.Protect(plaintext));
    }

    /// <summary>Attempts to unprotect and validate a callback token.</summary>
    public bool TryUnprotect(string? token, out NotionCallbackPayload? payload)
    {
        payload = null;

        if (string.IsNullOrWhiteSpace(token) || !Base64Url.IsValid(token))
        {
            return false;
        }

        try
        {
            var protectedBytes = Base64Url.DecodeFromChars(token);
            payload = JsonSerializer.Deserialize(
                _protector.Unprotect(protectedBytes),
                NotionWebhookJsonContext.Default.NotionCallbackPayload
            );

            if (payload is null || !IsValidPayload(payload))
            {
                payload = null;
                return false;
            }

            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void ValidatePayload(NotionCallbackPayload payload)
    {
        if (!IsValidPayload(payload))
        {
            throw new ArgumentException("Callback payload values are invalid.", nameof(payload));
        }
    }

    private static bool IsValidPayload(NotionCallbackPayload payload) =>
        !string.IsNullOrWhiteSpace(payload.OrganizationId)
        && !string.IsNullOrWhiteSpace(payload.LibraryId)
        && payload.CallbackTokenSerial >= 0;
}

/// <summary>Tenant and revocation state encoded in a Notion callback URL.</summary>
/// <param name="OrganizationId">Organization that owns the library.</param>
/// <param name="LibraryId">Library receiving the webhook.</param>
/// <param name="CallbackTokenSerial">Revocation generation captured when the URL was issued.</param>
public sealed record NotionCallbackPayload(
    string OrganizationId,
    string LibraryId,
    int CallbackTokenSerial
);
