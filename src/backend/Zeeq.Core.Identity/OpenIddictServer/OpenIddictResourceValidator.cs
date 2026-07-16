using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Zeeq.Core.Identity;

/// <summary>
/// Validates RFC 8707 resource indicators for the single local MCP resource.
/// </summary>
/// <remarks>
/// The productionized server rejects missing or mismatched resource indicators
/// instead of silently normalizing requests. Refresh-token requests may omit the
/// resource because OpenIddict can preserve the resource authorized on the
/// original grant; if a refresh request sends a resource, it still has to match.
/// </remarks>
public static class OpenIddictResourceValidator
{
    /// <summary>
    /// Validates authorization requests, where the MCP resource indicator is required.
    /// </summary>
    public static OpenIddictResourceValidationResult ValidateAuthorizationRequest(
        IEnumerable<string> resources,
        string expectedResource
    ) => Validate(resources, expectedResource, allowMissing: false);

    /// <summary>
    /// Validates token requests, allowing refresh-token exchanges to omit resource.
    /// </summary>
    public static OpenIddictResourceValidationResult ValidateTokenRequest(
        string? grantType,
        IEnumerable<string> resources,
        string expectedResource
    )
    {
        var allowMissing = string.Equals(
            grantType,
            GrantTypes.RefreshToken,
            StringComparison.OrdinalIgnoreCase
        );

        return Validate(resources, expectedResource, allowMissing);
    }

    private static OpenIddictResourceValidationResult Validate(
        IEnumerable<string> resources,
        string expectedResource,
        bool allowMissing
    )
    {
        var normalizedExpected = expectedResource.TrimEnd('/');
        var values = resources
            .Where(resource => !string.IsNullOrWhiteSpace(resource))
            .Select(resource => resource.TrimEnd('/'))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (values.Length == 0)
        {
            return allowMissing
                ? OpenIddictResourceValidationResult.Success
                : OpenIddictResourceValidationResult.Failed(
                    "MissingResource",
                    $"The request must include resource '{normalizedExpected}'."
                );
        }

        if (
            values.Length == 1
            && string.Equals(values[0], normalizedExpected, StringComparison.Ordinal)
        )
        {
            return OpenIddictResourceValidationResult.Success;
        }

        return OpenIddictResourceValidationResult.Failed(
            "InvalidResource",
            $"The request resource must be exactly '{normalizedExpected}'."
        );
    }
}

/// <summary>
/// Result returned by the OpenIddict resource validation helper.
/// </summary>
/// <param name="Succeeded">Whether resource validation succeeded.</param>
/// <param name="Code">Internal diagnostic code for failed validation.</param>
/// <param name="Description">OAuth error description to return to the client.</param>
public sealed record OpenIddictResourceValidationResult(
    bool Succeeded,
    string? Code,
    string? Description
)
{
    /// <summary>
    /// Successful resource validation result.
    /// </summary>
    public static OpenIddictResourceValidationResult Success { get; } = new(true, null, null);

    /// <summary>
    /// Creates a failed resource validation result.
    /// </summary>
    public static OpenIddictResourceValidationResult Failed(string code, string description) =>
        new(false, code, description);
}
