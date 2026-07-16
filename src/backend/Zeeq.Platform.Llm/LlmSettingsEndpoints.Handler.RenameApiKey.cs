using Zeeq.Core.Llm;
using Zeeq.Core.Models;

namespace Zeeq.Platform.Llm;

/// <summary>
/// Renames active tenant-owned LLM API keys.
/// </summary>
public sealed class RenameLlmApiKeyHandler(
    LlmSettingsAuthorization authorization,
    IEncryptedValueStore encryptedValues
) : IEndpointHandler
{
    /// <summary>
    /// Updates display metadata without touching ciphertext.
    /// </summary>
    public async Task<IResult> HandleAsync(
        string orgId,
        string keyId,
        RenameLlmApiKeyRequest request,
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

        var existing = await encryptedValues.FindActiveAsync(orgId, keyId, cancellationToken);

        if (existing is null || existing.Kind != EncryptedValueKind.LlmApiKey)
        {
            return TypedResults.NotFound();
        }

        existing.Name = string.IsNullOrWhiteSpace(request.Name) ? null : request.Name.Trim();

        existing.UpdatedAtUtc = DateTimeOffset.UtcNow;

        var updated = await encryptedValues.UpdateAsync(existing, cancellationToken);

        return updated
            ? TypedResults.Ok(LlmSettingsContractMapping.ToResponse(existing))
            : TypedResults.NotFound();
    }
}
