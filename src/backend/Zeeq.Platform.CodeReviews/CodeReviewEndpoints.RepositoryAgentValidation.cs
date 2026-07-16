using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Validates reviewer-agent API requests before they are written to storage.
/// </summary>
/// <remarks>
/// The persisted reviewer-agent row is operational configuration. These checks
/// keep invalid model tiers, empty prompts, and overlong JSONB activation rules
/// from escaping into the store where the runner would otherwise fail later.
/// </remarks>
internal static class CodeReviewerAgentEndpointValidation
{
    public static CodeReviewEndpointError? Validate(CreateCodeReviewerAgentRequest request) =>
        Validate(
            request.DisplayName,
            request.ReviewFacet,
            request.ModelTier,
            request.Prompt,
            request.ActivationConfiguration
        );

    public static CodeReviewEndpointError? Validate(UpdateCodeReviewerAgentRequest request) =>
        Validate(
            request.DisplayName,
            request.ReviewFacet,
            request.ModelTier,
            request.Prompt,
            request.ActivationConfiguration
        );

    private static CodeReviewEndpointError? Validate(
        string displayName,
        string reviewFacet,
        CodeReviewModelTier modelTier,
        string prompt,
        CodeReviewerActivationConfigurationDto activationConfiguration
    )
    {
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 256)
        {
            return new CodeReviewEndpointError(
                "invalid_agent",
                "Agent display name is required and must be 256 characters or fewer."
            );
        }

        if (string.IsNullOrWhiteSpace(reviewFacet) || reviewFacet.Trim().Length > 128)
        {
            return new CodeReviewEndpointError(
                "invalid_agent",
                "Agent review facet is required and must be 128 characters or fewer."
            );
        }

        if (!Enum.IsDefined(modelTier))
        {
            return new CodeReviewEndpointError("invalid_agent", "Agent model tier is invalid.");
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            return new CodeReviewEndpointError("invalid_agent", "Agent prompt is required.");
        }

        return ValidateActivationConfiguration(activationConfiguration);
    }

    private static CodeReviewEndpointError? ValidateActivationConfiguration(
        CodeReviewerActivationConfigurationDto activationConfiguration
    )
    {
        if (activationConfiguration is null)
        {
            return new CodeReviewEndpointError(
                "invalid_agent",
                "Agent activation configuration is required."
            );
        }

        var includedFiles = activationConfiguration.IncludedFiles ?? [];
        var excludedFiles = activationConfiguration.ExcludedFiles ?? [];
        var rules = includedFiles.Concat(excludedFiles);

        if (rules.Any(rule => string.IsNullOrWhiteSpace(rule.Pattern)))
        {
            return new CodeReviewEndpointError(
                "invalid_agent",
                "Agent activation file patterns cannot be empty."
            );
        }

        if (rules.Any(rule => rule.Pattern.Trim().Length > 1024))
        {
            return new CodeReviewEndpointError(
                "invalid_agent",
                "Agent activation file patterns must be 1024 characters or fewer."
            );
        }

        return rules.Any(rule => !Enum.IsDefined(rule.MatchType))
            ? new CodeReviewEndpointError(
                "invalid_agent",
                "Agent activation file match type is invalid."
            )
            : null;
    }
}
