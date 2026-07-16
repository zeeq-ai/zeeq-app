using Zeeq.Core.Llm;

namespace Zeeq.Platform.Llm;

/// <summary>
/// Rotates active tenant-owned LLM API keys.
/// </summary>
public sealed class RotateLlmApiKeyHandler(
    LlmSettingsAuthorization authorization,
    KeyEncryptionService keys
) : IEndpointHandler
{
    /// <summary>
    /// Replaces ciphertext with a newly encrypted plaintext key value.
    /// </summary>
    public async Task<IResult> HandleAsync(
        string orgId,
        string keyId,
        RotateLlmApiKeyRequest request,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var accessResult = await LlmSettingsHandlerSupport.RequireManagerAsync(
            authorization,
            orgId,
            user,
            cancellationToken
        );

        if (accessResult is not null)
        {
            return accessResult;
        }

        if (string.IsNullOrWhiteSpace(request.ApiKey))
        {
            return TypedResults.BadRequest(new LlmSettingsError("API key is required."));
        }

        var rotated = await keys.RotateKeyAsync(
            orgId,
            keyId,
            request.ApiKey,
            DateTimeOffset.UtcNow,
            cancellationToken
        );

        return rotated ? TypedResults.NoContent() : TypedResults.NotFound();
    }
}
