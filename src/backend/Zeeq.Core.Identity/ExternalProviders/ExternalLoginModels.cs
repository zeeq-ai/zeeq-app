using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zeeq.Core.Models;

namespace Zeeq.Core.Identity;

/// <summary>
/// Handles userinfo responses where the subject is a number (GitHub) or string (OIDC).
/// </summary>
internal sealed class StringOrNumberConverter : JsonConverter<string?>
{
    public override string? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    ) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.GetInt64().ToString(),
            JsonTokenType.Null => null,
            _ => throw new JsonException($"Unexpected token {reader.TokenType} for string field."),
        };

    public override void Write(
        Utf8JsonWriter writer,
        string? value,
        JsonSerializerOptions options
    ) => writer.WriteStringValue(value);
}

/// <summary>
/// Provider option returned to the browser login UI.
/// </summary>
/// <param name="Name">Stable local provider key used in login routes.</param>
/// <param name="DisplayName">User-facing provider name.</param>
/// <param name="Enabled">Whether configuration is complete enough to start login.</param>
/// <param name="LoginUrl">Relative URL that starts the provider login flow.</param>
internal sealed record ProviderSummary(
    string Name,
    string DisplayName,
    bool Enabled,
    string LoginUrl
);

// External OAuth/OIDC providers return protocol-defined payloads. Keep explicit
// JsonPropertyName attributes for those wire contracts; Zeeq-owned browser API
// DTOs below rely on the default camelCase JSON policy.

/// <summary>
/// Token response returned by an external OAuth/OIDC provider.
/// </summary>
/// <param name="AccessToken">Provider access token used only for userinfo lookup.</param>
/// <param name="IdToken">OIDC identity token used as the preferred identity proof.</param>
/// <param name="RefreshToken">Provider refresh token, currently ignored by the local app.</param>
/// <param name="ExpiresIn">Provider token lifetime in seconds, when supplied.</param>
/// <param name="TokenType">Provider token type, usually <c>Bearer</c>.</param>
internal sealed record ProviderTokenResponse(
    [property: JsonPropertyName("access_token")] string? AccessToken,
    [property: JsonPropertyName("id_token")] string? IdToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("expires_in")] long? ExpiresIn,
    [property: JsonPropertyName("token_type")] string? TokenType
);

/// <summary>
/// Userinfo payload from OIDC-style and GitHub-style external providers.
/// </summary>
/// <remarks>
/// The app prefers an OIDC <c>id_token</c> when present. Userinfo fills profile
/// gaps or acts as the fallback identity source for providers that do not return
/// an ID token in this flow.
/// </remarks>
internal sealed record ProviderUserInfo(
    [property: JsonPropertyName("sub")]
    [property: JsonConverter(typeof(StringOrNumberConverter))]
        string? Subject,
    [property: JsonPropertyName("id")]
    [property: JsonConverter(typeof(StringOrNumberConverter))]
        string? FallbackSubject,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("picture")] string? Picture,
    [property: JsonPropertyName("avatar_url")] string? AvatarUrl
)
{
    /// <summary>Effective subject — prefers OIDC <c>sub</c>, falls back to <c>id</c> (GitHub).</summary>
    public string? EffectiveSubject => Subject ?? FallbackSubject;

    /// <summary>
    /// Best-effort photo URL — prefers OIDC <c>picture</c> (Google), falls
    /// back to <c>avatar_url</c> (GitHub).
    /// </summary>
    public string? PictureUrl => Picture ?? AvatarUrl;
}

/// <summary>
/// Lightweight org summary for the <c>orgs[]</c> array in the /me response.
/// Includes display metadata for shell navigation, <see cref="IsDefault"/> for
/// default-org selection, and <see cref="Status"/> to distinguish active
/// memberships from pending invitations.
/// </summary>
internal sealed record OrgSummary(
    string Id,
    string? InvitationId,
    string? Slug,
    string DisplayName,
    string? IconUrl,
    string Role,
    bool IsDefault,
    MembershipStatus Status,
    DateTimeOffset? ActivatedAtUtc
);

/// <summary>
/// Org-scoped user alias exposed to the browser.
/// </summary>
/// <param name="Id">Stable alias row id.</param>
/// <param name="Kind">Alias namespace, such as <c>email</c> or <c>github</c>.</param>
/// <param name="Value">User-facing alias value.</param>
/// <param name="NormalizedValue">Server-normalized lookup value.</param>
/// <param name="VerifiedAtUtc">Timestamp when this alias was verified, if any.</param>
internal sealed record UserAliasDto(
    string Id,
    UserAliasKind Kind,
    string Value,
    string NormalizedValue,
    DateTimeOffset? VerifiedAtUtc
);

/// <summary>
/// Current user's aliases for the active organization.
/// </summary>
/// <param name="Aliases">Org-scoped aliases owned by the signed-in user.</param>
internal sealed record UserAliasesResponse(IReadOnlyList<UserAliasDto> Aliases);

/// <summary>
/// Replacement payload for the signed-in user's aliases in one organization.
/// </summary>
/// <param name="EmailAliases">Email aliases used to match telemetry owner emails.</param>
/// <param name="GitHubAliases">GitHub login aliases used to match pull requests and reviews.</param>
internal sealed record UpdateUserAliasesRequest(
    [property: MaxLength(UserAliasNormalizer.MaxAliasesPerKind)]
    [property: UserAliasItemMaxLength(UserAliasNormalizer.MaxAliasLength)]
        IReadOnlyList<string> EmailAliases,
    [property: MaxLength(UserAliasNormalizer.MaxAliasesPerKind)]
    [property: UserAliasItemMaxLength(UserAliasNormalizer.MaxAliasLength)]
        IReadOnlyList<string> GitHubAliases
);

/// <summary>
/// Validates that each alias value in a request array is present and bounded.
/// </summary>
internal sealed class UserAliasItemMaxLengthAttribute(int maximumLength) : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is not IEnumerable<string?> aliases)
        {
            return ValidationResult.Success;
        }

        return aliases.Any(alias =>
            string.IsNullOrWhiteSpace(alias) || alias.Length > maximumLength
        )
            ? new ValidationResult(
                $"Alias values must be non-empty and cannot exceed {maximumLength} characters."
            )
            : ValidationResult.Success;
    }
}

/// <summary>
/// Current browser identity response consumed by the web app.
/// </summary>
/// <remarks>
/// This combines cookie claims with fresh membership-store data. Roles and org
/// lists should be resolved at request time so changes do not require issuing a
/// new browser cookie.
/// </remarks>
internal sealed record MeResponse(
    string? UserId,
    string? Subject,
    string? OrganizationId,
    string? TeamId,
    string? Provider,
    string? ProviderSubject,
    string? Name,
    string? Email,
    string? PictureUrl,
    string? OrganizationRole,
    string? OrganizationSlug,
    IReadOnlyList<OrgSummary>? Organizations,
    IReadOnlyList<UserAliasDto> Aliases,
    bool IsSystemAdmin
);

internal static class UserAliasEndpointMapping
{
    public static UserAliasDto ToDto(UserAlias alias) =>
        new(alias.Id, alias.Kind, alias.DisplayValue, alias.NormalizedValue, alias.VerifiedAtUtc);
}
