using Zeeq.Core.Llm;

namespace Zeeq.Platform.Llm;

/// <summary>
/// Disables unreferenced tenant-owned LLM API keys.
/// </summary>
public sealed class DeleteLlmApiKeyHandler(
    LlmSettingsAuthorization authorization,
    ILlmSettingsStore settings,
    IEncryptedValueStore encryptedValues
) : IEndpointHandler
{
    /// <summary>
    /// Soft-disables an encrypted key after checking tier references.
    /// </summary>
    public async Task<IResult> HandleAsync(
        string orgId,
        string keyId,
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

        var configuration = await settings.FindConfigurationAsync(orgId, cancellationToken);

        if (configuration is null)
        {
            return Results.NotFound();
        }

        if (
            LlmSettingsContractMapping
                .ReferencedKeyIds(configuration)
                .Contains(keyId, StringComparer.Ordinal)
        )
        {
            return Results.BadRequest(
                new LlmSettingsError(
                    "Key cannot be deleted while it is referenced by LLM settings."
                )
            );
        }

        var disabled = await encryptedValues.DisableAsync(
            orgId,
            keyId,
            DateTimeOffset.UtcNow,
            cancellationToken
        );

        return disabled ? Results.NoContent() : Results.NotFound();
    }
}
