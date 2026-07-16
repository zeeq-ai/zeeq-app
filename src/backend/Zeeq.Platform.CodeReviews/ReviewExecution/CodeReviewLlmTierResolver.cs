using Zeeq.Core.Common;
using Zeeq.Core.Llm;
using Zeeq.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Resolves semantic code-review model tiers into per-run chat clients.
/// </summary>
/// <remarks>
/// Reviewer-agent rows intentionally store only <see cref="CodeReviewModelTier" />.
/// Provider, model, endpoint, and credential material are resolved at execution
/// time from organization LLM settings or app-level defaults.
///
/// Resolution has three credential paths:
///
/// 1. No organization LLM configuration exists. The resolver returns the
/// already-registered app default client for the requested tier. This path does
/// not construct a new provider client because the system default clients are
/// created from application settings during service setup.
///
/// 2. Organization LLM configuration exists and the selected tier references a
/// tenant-managed encrypted key. The resolver decrypts that key for this run,
/// builds a tenant-scoped <see cref="ResolvedLlmConfiguration" />, and asks
/// <see cref="ILlmClientFactory" /> to create a fresh chat client. The decrypted
/// key is never returned by this class, stored on reviewer rows, or cached here.
///
/// 3. Organization LLM configuration exists and the selected tier has no key.
/// Only Fireworks may use this path because Zeeq's internal app-level default
/// key is currently a Fireworks key. The resolver still honors the organization
/// model selection, but supplies the app-level key and endpoint through the
/// default-client factory path.
///
/// The returned <see cref="CodeReviewResolvedLlmClient" /> includes only
/// non-secret metadata so later workflow code can record which tier/provider
/// branch was used without exposing credential material.
/// </remarks>
public sealed partial class CodeReviewLlmTierResolver(
    ILlmSettingsStore settingsStore,
    KeyEncryptionService keyEncryption,
    ILlmClientFactory clientFactory,
    DefaultLlmChatClients defaultClients,
    LlmSettings llmSettings,
    ILogger<CodeReviewLlmTierResolver> logger
)
{
    /// <summary>
    /// Resolves a reviewer tier for one organization without exposing API key material.
    /// </summary>
    /// <remarks>
    /// The lookup starts with the organization settings store because
    /// organization-owned configuration takes precedence over system defaults.
    /// A missing configuration is a normal state for new organizations, so the
    /// resolver falls back to the prebuilt app default client for the same
    /// semantic tier.
    ///
    /// When organization configuration exists, the selected Fast/High/Max tier
    /// must contain a provider and model. If the tier contains a <c>KeyId</c>,
    /// the key is decrypted only long enough to build the run-local chat client.
    /// If the tier does not contain a <c>KeyId</c>, the resolver delegates to the
    /// internal-default-key path, which enforces the provider allow-list before
    /// supplying Zeeq's app-level credential.
    ///
    /// This method deliberately resolves to <see cref="IChatClient" /> rather
    /// than mutating the runtime reviewer agent. The reviewer-agent contract
    /// remains provider-neutral, and future Agent Framework workflow code can
    /// wrap the returned client with reviewer-specific instructions.
    /// </remarks>
    public async Task<CodeReviewResolvedLlmClient> ResolveChatClientAsync(
        string organizationId,
        CodeReviewModelTier tier,
        CancellationToken cancellationToken
    )
    {
        LogTierResolutionStarted(logger, organizationId, tier);
        var organizationConfiguration = await settingsStore.FindConfigurationAsync(
            organizationId,
            cancellationToken
        );

        if (organizationConfiguration is null)
        {
            var defaultModel = DefaultModelForTier(tier);
            LogTierResolved(
                logger,
                organizationId,
                tier,
                defaultModel.Provider,
                defaultModel.Model,
                CodeReviewLlmCredentialSource.SystemDefault
            );

            return new(
                DefaultClientForTier(tier),
                tier,
                defaultModel.Provider,
                defaultModel.Model,
                CodeReviewLlmCredentialSource.SystemDefault
            );
        }

        LogOrganizationLlmConfigurationFound(logger, organizationId, tier);
        var tierConfiguration = OrganizationTierConfiguration(organizationConfiguration, tier);
        ValidateTierConfiguration(tierConfiguration, tier);

        if (string.IsNullOrWhiteSpace(tierConfiguration.KeyId))
        {
            return ResolveWithInternalDefaultKey(organizationId, tier, tierConfiguration);
        }

        var keyId = tierConfiguration.KeyId.Trim();
        var apiKey = await keyEncryption.DecryptKeyAsync(organizationId, keyId, cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            LogManagedKeyUnavailable(logger, organizationId, tier);

            throw new InvalidOperationException(
                $"LLM API key '{keyId}' configured for code-review tier '{tier}' was not found or is not active."
            );
        }

        var resolvedConfiguration = new ResolvedLlmConfiguration(
            tierConfiguration.Provider,
            tierConfiguration.Model,
            apiKey,
            KeySource: "organization-managed-key",
            Endpoint: !string.IsNullOrWhiteSpace(tierConfiguration.Endpoint)
                ? tierConfiguration.Endpoint.Trim()
                : EndpointForProvider(tierConfiguration.Provider, tier)
        );
        LogTierResolved(
            logger,
            organizationId,
            tier,
            resolvedConfiguration.Provider,
            resolvedConfiguration.Model,
            CodeReviewLlmCredentialSource.OrganizationManagedKey
        );

        return new(
            clientFactory.CreateChatClient(resolvedConfiguration),
            tier,
            resolvedConfiguration.Provider,
            resolvedConfiguration.Model,
            CodeReviewLlmCredentialSource.OrganizationManagedKey
        );
    }

    /// <summary>
    /// Resolves an organization tier that selected Zeeq's internal default key.
    /// </summary>
    /// <remarks>
    /// The organization settings UI allows a blank key only for providers that
    /// can safely use Zeeq's internal default key. In this phase that means
    /// Fireworks. This branch therefore keeps organization control over provider
    /// and model selection, but takes the actual API key from the app-level
    /// default model for the same semantic tier.
    ///
    /// Provider and model are normalized before constructing the default model
    /// object so harmless whitespace in persisted JSON does not leak into client
    /// creation or non-secret metadata.
    /// </remarks>
    private CodeReviewResolvedLlmClient ResolveWithInternalDefaultKey(
        string organizationId,
        CodeReviewModelTier tier,
        OrganizationLlmTierConfiguration tierConfiguration
    )
    {
        var provider = tierConfiguration.Provider;
        var modelName = tierConfiguration.Model;

        if (!OrganizationLlmTierConfiguration.CanUseInternalDefaultKey(provider))
        {
            LogInternalDefaultKeyRejected(logger, organizationId, tier, provider);

            throw new InvalidOperationException(
                $"Code-review tier '{tier}' requires a managed API key for provider '{provider}'."
            );
        }

        var defaultModel = DefaultModelForTier(tier);
        var model = new LlmModelDefault
        {
            Provider = provider,
            Model = modelName,
            ApiKey = defaultModel.ApiKey,
            Endpoint = EndpointForProvider(provider, tier),
        };
        LogTierResolved(
            logger,
            organizationId,
            tier,
            model.Provider,
            model.Model,
            CodeReviewLlmCredentialSource.InternalDefaultKey
        );

        return new(
            clientFactory.CreateDefaultChatClient(model),
            tier,
            model.Provider,
            model.Model,
            CodeReviewLlmCredentialSource.InternalDefaultKey
        );
    }

    /// <summary>
    /// Returns the prebuilt app-level default chat client for the semantic tier.
    /// </summary>
    /// <remarks>
    /// This is used only when the organization has no saved LLM configuration.
    /// The clients are already registered by shared LLM setup from application
    /// settings, so this branch avoids recreating clients or touching key
    /// material in the resolver.
    /// </remarks>
    private IChatClient DefaultClientForTier(CodeReviewModelTier tier) =>
        tier switch
        {
            CodeReviewModelTier.Fast => defaultClients.Fast,
            CodeReviewModelTier.High => defaultClients.High,
            CodeReviewModelTier.Max => defaultClients.Max,
            _ => throw new ArgumentOutOfRangeException(
                nameof(tier),
                tier,
                "Unknown code-review model tier."
            ),
        };

    /// <summary>
    /// Returns the app-level default model settings for the semantic tier.
    /// </summary>
    /// <remarks>
    /// The model settings provide non-secret metadata for the system-default
    /// branch and supply app-level API key/endpoint values when an organization
    /// Fireworks tier intentionally uses Zeeq's internal key.
    /// </remarks>
    private LlmModelDefault DefaultModelForTier(CodeReviewModelTier tier) =>
        tier switch
        {
            CodeReviewModelTier.Fast => llmSettings.Models.Fast,
            CodeReviewModelTier.High => llmSettings.Models.High,
            CodeReviewModelTier.Max => llmSettings.Models.Max,
            _ => throw new ArgumentOutOfRangeException(
                nameof(tier),
                tier,
                "Unknown code-review model tier."
            ),
        };

    /// <summary>
    /// Selects the organization's Fast, High, or Max configuration for the reviewer tier.
    /// </summary>
    /// <remarks>
    /// Code-review agents use <see cref="CodeReviewModelTier" /> so that saved
    /// agents remain independent of concrete provider names and model ids. This
    /// method is the narrow mapping point from that code-review tier to the
    /// organization LLM settings document.
    /// </remarks>
    private static OrganizationLlmTierConfiguration OrganizationTierConfiguration(
        OrganizationLlmConfiguration configuration,
        CodeReviewModelTier tier
    ) =>
        tier switch
        {
            CodeReviewModelTier.Fast => configuration.Fast,
            CodeReviewModelTier.High => configuration.High,
            CodeReviewModelTier.Max => configuration.Max,
            _ => throw new ArgumentOutOfRangeException(
                nameof(tier),
                tier,
                "Unknown code-review model tier."
            ),
        };

    /// <summary>
    /// Validates the minimum provider/model shape required for runtime resolution.
    /// </summary>
    /// <remarks>
    /// Key validation is handled by the branch that knows which credential path
    /// is being used. This method only checks the provider and model because
    /// those values are required for both tenant-key and internal-default-key
    /// paths, and they produce clearer errors here than inside the provider SDK.
    /// </remarks>
    private static void ValidateTierConfiguration(
        OrganizationLlmTierConfiguration configuration,
        CodeReviewModelTier tier
    )
    {
        if (string.IsNullOrWhiteSpace(configuration.Provider))
        {
            throw new InvalidOperationException(
                $"LLM provider is not configured for code-review tier '{tier}'."
            );
        }

        if (string.IsNullOrWhiteSpace(configuration.Model))
        {
            throw new InvalidOperationException(
                $"LLM model is not configured for code-review tier '{tier}'."
            );
        }
    }

    /// <summary>
    /// Resolves the OpenAI-compatible endpoint for an organization provider selection.
    /// </summary>
    /// <remarks>
    /// Organization LLM settings store provider/model/key metadata only; endpoint
    /// routing remains application-owned so tenant settings cannot redirect SDK
    /// traffic. The resolver first prefers the default endpoint for the same
    /// semantic tier, then falls back to any configured default model with the
    /// same provider. Providers without an app-level endpoint use the SDK
    /// default by returning an empty string.
    /// </remarks>
    private string EndpointForProvider(string provider, CodeReviewModelTier tier)
    {
        var tierDefault = DefaultModelForTier(tier);

        if (provider.Equals(tierDefault.Provider, StringComparison.OrdinalIgnoreCase))
        {
            return tierDefault.Endpoint;
        }

        foreach (var modelDefault in EnumerateDefaultModels())
        {
            if (
                provider.Equals(modelDefault.Provider, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(modelDefault.Endpoint)
            )
            {
                return modelDefault.Endpoint;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Enumerates app-level default model settings used for provider endpoint lookup.
    /// </summary>
    /// <remarks>
    /// The endpoint lookup is intentionally tiny and local to this resolver. It
    /// avoids introducing a shared abstraction before another caller needs the
    /// same behavior while still keeping endpoint selection consistent across
    /// Fast, High, and Max defaults.
    /// </remarks>
    private IEnumerable<LlmModelDefault> EnumerateDefaultModels()
    {
        yield return llmSettings.Models.Fast;
        yield return llmSettings.Models.High;
        yield return llmSettings.Models.Max;
    }

    [LoggerMessage(
        EventId = 3280,
        Level = LogLevel.Information,
        Message = "Resolving code-review LLM tier. OrganizationId={OrganizationId}, Tier={Tier}"
    )]
    private static partial void LogTierResolutionStarted(
        ILogger logger,
        string organizationId,
        CodeReviewModelTier tier
    );

    [LoggerMessage(
        EventId = 3281,
        Level = LogLevel.Information,
        Message = "Found organization LLM configuration for code-review tier. OrganizationId={OrganizationId}, Tier={Tier}"
    )]
    private static partial void LogOrganizationLlmConfigurationFound(
        ILogger logger,
        string organizationId,
        CodeReviewModelTier tier
    );

    [LoggerMessage(
        EventId = 3282,
        Level = LogLevel.Information,
        Message = "Resolved code-review LLM tier. OrganizationId={OrganizationId}, Tier={Tier}, Provider={Provider}, Model={Model}, CredentialSource={CredentialSource}"
    )]
    private static partial void LogTierResolved(
        ILogger logger,
        string organizationId,
        CodeReviewModelTier tier,
        string provider,
        string model,
        CodeReviewLlmCredentialSource credentialSource
    );

    [LoggerMessage(
        EventId = 3283,
        Level = LogLevel.Warning,
        Message = "Configured organization-managed code-review LLM key is missing or inactive. OrganizationId={OrganizationId}, Tier={Tier}"
    )]
    private static partial void LogManagedKeyUnavailable(
        ILogger logger,
        string organizationId,
        CodeReviewModelTier tier
    );

    [LoggerMessage(
        EventId = 3284,
        Level = LogLevel.Warning,
        Message = "Rejected internal default key for unsupported code-review provider. OrganizationId={OrganizationId}, Tier={Tier}, Provider={Provider}"
    )]
    private static partial void LogInternalDefaultKeyRejected(
        ILogger logger,
        string organizationId,
        CodeReviewModelTier tier,
        string provider
    );
}

/// <summary>
/// Resolved chat client and non-secret metadata for a code-review model tier.
/// </summary>
/// <remarks>
/// This record is the handoff from tier resolution into workflow execution. It
/// carries the client to invoke plus provider/model/source labels that are safe
/// for logs, metrics, and artifact metadata. It must not gain API key, encrypted
/// value id, or other credential-bearing fields.
/// </remarks>
/// <param name="ChatClient">Per-run or registered app-level chat client to use for the reviewer.</param>
/// <param name="Tier">Semantic code-review tier requested by the reviewer agent.</param>
/// <param name="Provider">Normalized provider name selected for this run.</param>
/// <param name="Model">Normalized provider model id selected for this run.</param>
/// <param name="CredentialSource">Non-secret label for the credential path used to create the client.</param>
public sealed record CodeReviewResolvedLlmClient(
    IChatClient ChatClient,
    CodeReviewModelTier Tier,
    string Provider,
    string Model,
    CodeReviewLlmCredentialSource CredentialSource
);

/// <summary>
/// Non-secret credential source used to create a code-review LLM client.
/// </summary>
/// <remarks>
/// These values describe resolution behavior, not credential identity. They are
/// intentionally safe to emit in diagnostic metadata because they do not include
/// API keys or tenant encrypted-value ids.
/// </remarks>
public enum CodeReviewLlmCredentialSource
{
    /// <summary>Registered app-level default tier client.</summary>
    /// <remarks>
    /// Used when an organization has not saved an LLM configuration. The client
    /// comes directly from DI and was created from application settings.
    /// </remarks>
    SystemDefault,

    /// <summary>Organization tier using the app-level internal key for an allowed provider.</summary>
    /// <remarks>
    /// Used when organization settings choose an allowed provider/model without
    /// a tenant key. Today that means a DeepSeek model with Zeeq's internal
    /// default key.
    /// </remarks>
    InternalDefaultKey,

    /// <summary>Organization tier using a decrypted tenant-owned key for the current run.</summary>
    /// <remarks>
    /// Used when organization settings reference an active encrypted LLM API key
    /// row. The key is decrypted immediately before client construction and is
    /// not exposed through the resolver result.
    /// </remarks>
    OrganizationManagedKey,
}
