using Zeeq.Core.Common;
using Zeeq.Core.Llm;
using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

public sealed partial class CodeReviewLlmTierResolver
{
    /// <summary>Captures a tier mapping without decrypting its credential.</summary>
    public async Task<CodeReviewEvaluationModelSelection> CaptureAsync(
        string organizationId,
        CodeReviewModelTier tier,
        CancellationToken cancellationToken
    )
    {
        var configuration = await settingsStore.FindConfigurationAsync(
            organizationId,
            cancellationToken
        );
        if (configuration is null)
        {
            var model = DefaultModelForTier(tier);
            return new(
                tier,
                model.Provider,
                model.Model,
                model.Endpoint,
                CodeReviewEvaluationCredentialSource.SystemDefault,
                null
            );
        }

        var selected = OrganizationTierConfiguration(configuration, tier);
        ValidateTierConfiguration(selected, tier);
        var keyId = string.IsNullOrWhiteSpace(selected.KeyId) ? null : selected.KeyId.Trim();
        if (
            keyId is null
            && !OrganizationLlmTierConfiguration.CanUseInternalDefaultKey(selected.Provider)
        )
        {
            throw new InvalidOperationException(
                $"Code-review tier '{tier}' requires a managed API key."
            );
        }

        var endpoint =
            keyId is not null && !string.IsNullOrWhiteSpace(selected.Endpoint)
                ? selected.Endpoint.Trim()
                : EndpointForProvider(selected.Provider, tier);
        return new(
            tier,
            selected.Provider,
            selected.Model,
            endpoint,
            keyId is null
                ? CodeReviewEvaluationCredentialSource.InternalDefaultKey
                : CodeReviewEvaluationCredentialSource.OrganizationManagedKey,
            keyId
        );
    }

    /// <summary>Constructs a client from the captured mapping without rereading organization tiers.</summary>
    public async Task<CodeReviewResolvedLlmClient> ResolveFrozenAsync(
        string organizationId,
        CodeReviewEvaluationModelSelection descriptor,
        CancellationToken cancellationToken
    )
    {
        string apiKey;
        if (
            descriptor.CredentialSource
            == CodeReviewEvaluationCredentialSource.OrganizationManagedKey
        )
        {
            if (string.IsNullOrWhiteSpace(descriptor.OrganizationKeyId))
                throw new InvalidOperationException(
                    "Captured managed credential reference is missing."
                );
            apiKey = await keyEncryption.DecryptKeyAsync(
                organizationId,
                descriptor.OrganizationKeyId,
                cancellationToken
            ) ?? throw new InvalidOperationException("Captured managed credential is unavailable.");
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("Captured managed credential is unavailable.");
        }
        else if (
            descriptor.CredentialSource
            is CodeReviewEvaluationCredentialSource.SystemDefault
                or CodeReviewEvaluationCredentialSource.InternalDefaultKey
        )
        {
            if (descriptor.OrganizationKeyId is not null)
                throw new InvalidOperationException(
                    "Captured default credential must not reference an organization key."
                );
            if (
                descriptor.CredentialSource
                    == CodeReviewEvaluationCredentialSource.InternalDefaultKey
                && !OrganizationLlmTierConfiguration.CanUseInternalDefaultKey(descriptor.Provider)
            )
                throw new InvalidOperationException(
                    "Captured provider cannot use the internal credential."
                );
            var currentDefault = DefaultModelForTier(descriptor.Tier);
            if (
                !string.Equals(
                    currentDefault.Provider,
                    descriptor.Provider,
                    StringComparison.OrdinalIgnoreCase
                )
            )
                throw new InvalidOperationException(
                    "Captured provider's default credential is no longer available for this tier."
                );
            apiKey = currentDefault.ApiKey;
        }
        else
        {
            throw new InvalidOperationException("Unknown captured credential source.");
        }

        var client =
            descriptor.CredentialSource
            == CodeReviewEvaluationCredentialSource.OrganizationManagedKey
                ? clientFactory.CreateChatClient(
                    new ResolvedLlmConfiguration(
                        descriptor.Provider,
                        descriptor.Model,
                        apiKey,
                        KeySource: "organization-managed-key",
                        Endpoint: descriptor.Endpoint ?? ""
                    )
                )
                : clientFactory.CreateDefaultChatClient(
                    new LlmModelDefault
                    {
                        Provider = descriptor.Provider,
                        Model = descriptor.Model,
                        ApiKey = apiKey,
                        Endpoint = descriptor.Endpoint ?? "",
                    }
                );
        var source = descriptor.CredentialSource switch
        {
            CodeReviewEvaluationCredentialSource.SystemDefault =>
                CodeReviewLlmCredentialSource.SystemDefault,
            CodeReviewEvaluationCredentialSource.InternalDefaultKey =>
                CodeReviewLlmCredentialSource.InternalDefaultKey,
            CodeReviewEvaluationCredentialSource.OrganizationManagedKey =>
                CodeReviewLlmCredentialSource.OrganizationManagedKey,
            _ => throw new InvalidOperationException("Unknown captured credential source."),
        };
        return new(client, descriptor.Tier, descriptor.Provider, descriptor.Model, source);
    }
}
