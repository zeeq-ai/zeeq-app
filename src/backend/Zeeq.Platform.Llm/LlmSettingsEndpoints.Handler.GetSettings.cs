using Zeeq.Core.Llm;
using Zeeq.Core.Models;

namespace Zeeq.Platform.Llm;

/// <summary>
/// Returns LLM settings route state and, for managers, settings data.
/// </summary>
public sealed class GetLlmSettingsHandler(
    LlmSettingsAuthorization authorization,
    ILlmSettingsStore settings,
    IEncryptedValueStore encryptedValues
) : IEndpointHandler
{
    /// <summary>
    /// Handles the route state request.
    /// </summary>
    public async Task<IResult> HandleAsync(
        string orgId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var access = await authorization.ResolveAsync(orgId, user, cancellationToken);
        if (access is null)
        {
            return Results.NotFound();
        }

        if (!access.CanManage)
        {
            return Results.Ok(
                new LlmSettingsViewResponse(
                    CanManage: false,
                    Notice: "This view requires admin or owner access.",
                    Configuration: null,
                    Keys: []
                )
            );
        }

        return Results.Ok(
            await BuildManagerViewAsync(orgId, settings, encryptedValues, cancellationToken)
        );
    }

    internal static async Task<LlmSettingsViewResponse> BuildManagerViewAsync(
        string orgId,
        ILlmSettingsStore settings,
        IEncryptedValueStore encryptedValues,
        CancellationToken cancellationToken
    )
    {
        var configuration = await settings.FindConfigurationAsync(orgId, cancellationToken);
        if (configuration is null)
        {
            return new LlmSettingsViewResponse(
                CanManage: true,
                Notice: null,
                Configuration: null,
                Keys: []
            );
        }

        var keys = await encryptedValues.ListActiveAsync(
            orgId,
            EncryptedValueKind.LlmApiKey,
            cancellationToken
        );

        return new LlmSettingsViewResponse(
            CanManage: true,
            Notice: null,
            Configuration: LlmSettingsContractMapping.ToResponse(configuration),
            Keys: keys.Select(LlmSettingsContractMapping.ToResponse).ToArray()
        );
    }
}
