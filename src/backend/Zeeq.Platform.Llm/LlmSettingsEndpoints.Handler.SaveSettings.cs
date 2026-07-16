using Zeeq.Core.Llm;

namespace Zeeq.Platform.Llm;

/// <summary>
/// Saves organization LLM tier settings.
/// </summary>
public sealed class SaveLlmSettingsHandler(
    LlmSettingsAuthorization authorization,
    ILlmSettingsStore settings,
    IEncryptedValueStore encryptedValues
) : IEndpointHandler
{
    /// <summary>
    /// Validates key references and persists the configuration.
    /// </summary>
    public async Task<IResult> HandleAsync(
        string orgId,
        SaveLlmSettingsRequest request,
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

        if (!LlmSettingsHandlerSupport.TryValidate(request, out var validationError))
        {
            return TypedResults.BadRequest(new LlmSettingsError(validationError));
        }

        var configuration = LlmSettingsContractMapping.ToConfiguration(request);

        var keyError = await LlmSettingsHandlerSupport.ValidateReferencedKeysAsync(
            orgId,
            configuration,
            encryptedValues,
            cancellationToken
        );

        if (keyError is not null)
        {
            return TypedResults.BadRequest(new LlmSettingsError(keyError));
        }

        var saved = await settings.UpdateConfigurationAsync(
            orgId,
            configuration,
            DateTimeOffset.UtcNow,
            cancellationToken
        );

        return saved
            ? TypedResults.Ok(
                await GetLlmSettingsHandler.BuildManagerViewAsync(
                    orgId,
                    settings,
                    encryptedValues,
                    cancellationToken
                )
            )
            : TypedResults.NotFound();
    }
}
