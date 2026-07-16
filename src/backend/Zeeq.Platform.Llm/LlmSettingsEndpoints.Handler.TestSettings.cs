using Zeeq.Core.Common;
using Zeeq.Core.Llm;

namespace Zeeq.Platform.Llm;

/// <summary>
/// Runs bounded provider access tests for owner/admin users.
/// </summary>
public sealed class TestLlmSettingsHandler(
    LlmSettingsAuthorization authorization,
    LlmSettings defaultSettings,
    KeyEncryptionService keys,
    ILlmProviderAccessTester tester
) : IEndpointHandler
{
    /// <summary>
    /// Resolves either default or tenant-owned credentials and runs the access test.
    /// </summary>
    public async Task<IResult> HandleAsync(
        string orgId,
        TestLlmSettingsRequest request,
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

        if (string.IsNullOrWhiteSpace(request.Provider) || string.IsNullOrWhiteSpace(request.Model))
        {
            return TypedResults.BadRequest(
                new LlmSettingsError("Provider and model are required.")
            );
        }

        if (
            string.IsNullOrWhiteSpace(request.KeyId)
            && !LlmSettingsHandlerSupport.CanUseInternalDefaultKey(request.Provider)
        )
        {
            return TypedResults.BadRequest(
                new LlmSettingsError(
                    $"A managed API key is required for {request.Provider.Trim()}."
                )
            );
        }

        var apiKey = await ResolveApiKeyAsync(orgId, request, cancellationToken);

        if (apiKey is null)
        {
            return TypedResults.BadRequest(
                new LlmSettingsError("Referenced API key was not found.")
            );
        }

        var endpoint = !string.IsNullOrWhiteSpace(request.Endpoint)
            ? request.Endpoint.Trim()
            : LlmSettingsHandlerSupport.EndpointForProvider(
                defaultSettings,
                request.Provider,
                request.Tier
            );

        var result = await tester.TestAsync(
            new ResolvedLlmConfiguration(
                request.Provider.Trim(),
                request.Model.Trim(),
                apiKey,
                string.IsNullOrWhiteSpace(request.KeyId) ? "default" : "tenant-key",
                endpoint
            ),
            request.Prompt,
            cancellationToken
        );

        return TypedResults.Ok(result);
    }

    private async Task<string?> ResolveApiKeyAsync(
        string orgId,
        TestLlmSettingsRequest request,
        CancellationToken cancellationToken
    )
    {
        if (!string.IsNullOrWhiteSpace(request.KeyId))
        {
            return await keys.DecryptKeyAsync(orgId, request.KeyId.Trim(), cancellationToken);
        }

        return LlmSettingsHandlerSupport.DefaultApiKeyForTier(defaultSettings, request.Tier);
    }
}
