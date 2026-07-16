using System.ComponentModel.DataAnnotations;

namespace Zeeq.Core.Identity;

/// <summary>
/// Browser request body for creating a user-owned OAuth client credential.
/// </summary>
/// <param name="DisplayName">User-facing name shown in the credential management UI.</param>
public sealed record ClientCredentialCreateRequest(
    [property: Required, MaxLength(200)] string DisplayName
);

/// <summary>
/// Non-sensitive credential metadata returned by list endpoints.
/// </summary>
/// <param name="ClientId">OpenIddict client ID and local credential row ID.</param>
/// <param name="DisplayName">User-facing credential name.</param>
/// <param name="CreatedAtUtc">Timestamp when the credential was created.</param>
/// <param name="RevokedAtUtc">Timestamp when the credential was revoked, if any.</param>
public sealed record ClientCredentialSummary(
    string ClientId,
    string DisplayName,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? RevokedAtUtc
);

/// <summary>
/// One-time creation response containing the generated client secret.
/// </summary>
/// <remarks>
/// This is the only response shape that may include <see cref="ClientSecret"/>.
/// Store/list flows must return <see cref="ClientCredentialSummary"/> instead.
/// </remarks>
/// <param name="ClientId">OpenIddict client ID to use at the token endpoint.</param>
/// <param name="ClientSecret">Generated secret shown once to the creating user.</param>
/// <param name="DisplayName">User-facing credential name.</param>
/// <param name="CreatedAtUtc">Timestamp when the credential was created.</param>
/// <param name="TokenUrl">OpenIddict token endpoint URL.</param>
/// <param name="Scope">Required OAuth scope for MCP tool access.</param>
/// <param name="Resource">RFC 8707 resource indicator for the local MCP resource.</param>
/// <param name="SampleCurl">Diagnostic curl command for local setup and copy/paste testing.</param>
public sealed record ClientCredentialCreated(
    string ClientId,
    string ClientSecret,
    string DisplayName,
    DateTimeOffset CreatedAtUtc,
    string TokenUrl,
    string Scope,
    string Resource,
    string SampleCurl
)
{
    /// <summary>
    /// Builds the creation response and sample token request from current auth settings.
    /// </summary>
    public static ClientCredentialCreated Create(
        string clientId,
        string clientSecret,
        string displayName,
        DateTimeOffset createdAt,
        AuthSettings settings
    )
    {
        var tokenUrl = settings.IssuerTrimmed + "/connect/token";

        var sampleCurl =
            $"curl -u \"{clientId}:{clientSecret}\" "
            + "-d grant_type=client_credentials "
            + "-d scope=mcp:tools "
            + $"-d resource={settings.ResourceTrimmed} "
            + tokenUrl;

        return new(
            ClientId: clientId,
            ClientSecret: clientSecret,
            DisplayName: displayName,
            CreatedAtUtc: createdAt,
            TokenUrl: tokenUrl,
            Scope: "mcp:tools",
            Resource: settings.ResourceTrimmed,
            SampleCurl: sampleCurl
        );
    }
}
