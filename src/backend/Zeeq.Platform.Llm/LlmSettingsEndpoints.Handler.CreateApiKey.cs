using Zeeq.Core.Llm;

namespace Zeeq.Platform.Llm;

/// <summary>
/// Creates encrypted tenant-owned LLM API keys.
/// </summary>
public sealed class CreateLlmApiKeyHandler(
    LlmSettingsAuthorization authorization,
    KeyEncryptionService keys
) : IEndpointHandler
{
    /// <summary>
    /// Encrypts plaintext key material and returns metadata only.
    /// </summary>
    public async Task<IResult> HandleAsync(
        string orgId,
        CreateLlmApiKeyRequest request,
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

        var encrypted = await keys.EncryptAndStoreKeyAsync(
            orgId,
            request.Name,
            request.ApiKey,
            DateTimeOffset.UtcNow,
            cancellationToken
        );

        return TypedResults.Created(
            $"/api/v1/orgs/{Uri.EscapeDataString(orgId)}/llm-settings/keys/{Uri.EscapeDataString(encrypted.Id)}",
            LlmSettingsContractMapping.ToResponse(encrypted)
        );
    }
}
