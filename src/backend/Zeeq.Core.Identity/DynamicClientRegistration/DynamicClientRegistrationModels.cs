using System.Text.Json.Serialization;

namespace Zeeq.Core.Identity;

// These records implement RFC 7591/OAuth payloads, where snake_case field names
// are part of the protocol. Keep explicit JsonPropertyName here; Zeeq-owned
// browser API DTOs should rely on the default camelCase JSON policy instead.

/// <summary>
/// RFC 7591 Dynamic Client Registration request shape accepted by local MCP clients.
/// </summary>
/// <remarks>
/// The implementation intentionally supports the public/native subset used by
/// coding-agent and MCP Inspector clients: authorization code + PKCE with loopback,
/// HTTPS, or Cursor private-scheme redirect URIs. Confidential clients are created
/// through the separate browser-authenticated client-credential management API.
/// </remarks>
/// <param name="ClientName">Optional display name for the registered MCP client.</param>
/// <param name="RedirectUris">Redirect URIs the OAuth authorization code may return to.</param>
/// <param name="GrantTypes">Requested grant types; defaults to authorization code.</param>
/// <param name="ResponseTypes">Requested response types; defaults to code.</param>
/// <param name="Scope">Space-delimited requested scopes.</param>
/// <param name="TokenEndpointAuthMethod">Client authentication method; only <c>none</c> is supported.</param>
/// <param name="ApplicationType">OAuth application type; only <c>native</c> is supported.</param>
/// <param name="ClientUri">Optional client metadata URI supplied by RFC 7591 clients.</param>
public sealed record DynamicClientRegistrationRequest(
    [property: JsonPropertyName("client_name")] string? ClientName,
    [property: JsonPropertyName("redirect_uris")] IReadOnlyList<string>? RedirectUris,
    [property: JsonPropertyName("grant_types")] IReadOnlyList<string>? GrantTypes,
    [property: JsonPropertyName("response_types")] IReadOnlyList<string>? ResponseTypes,
    [property: JsonPropertyName("scope")] string? Scope,
    [property: JsonPropertyName("token_endpoint_auth_method")] string? TokenEndpointAuthMethod,
    [property: JsonPropertyName("application_type")] string? ApplicationType,
    [property: JsonPropertyName("client_uri")] string? ClientUri
);

/// <summary>
/// Dynamic Client Registration response returned after OpenIddict application creation.
/// </summary>
/// <param name="ClientId">Generated public client ID.</param>
/// <param name="ClientIdIssuedAt">Unix timestamp when the client ID was issued.</param>
/// <param name="ClientName">Display name stored for the client.</param>
/// <param name="RedirectUris">Registered redirect URIs.</param>
/// <param name="GrantTypes">Grant types the client may use.</param>
/// <param name="ResponseTypes">Response types the client may request.</param>
/// <param name="Scope">Space-delimited scopes registered on the client.</param>
/// <param name="TokenEndpointAuthMethod">Authentication method for the public client.</param>
/// <param name="ApplicationType">Registered OAuth application type.</param>
public sealed record DynamicClientRegistrationResponse(
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("client_id_issued_at")] long ClientIdIssuedAt,
    [property: JsonPropertyName("client_name")] string? ClientName,
    [property: JsonPropertyName("redirect_uris")] IReadOnlyList<string> RedirectUris,
    [property: JsonPropertyName("grant_types")] IReadOnlyList<string> GrantTypes,
    [property: JsonPropertyName("response_types")] IReadOnlyList<string> ResponseTypes,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("token_endpoint_auth_method")] string TokenEndpointAuthMethod,
    [property: JsonPropertyName("application_type")] string ApplicationType
);

/// <summary>
/// OAuth-style error payload returned by auth and DCR endpoints.
/// </summary>
/// <param name="Error">Machine-readable OAuth error code.</param>
/// <param name="ErrorDescription">Human-readable diagnostic description.</param>
public sealed record OAuthError(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("error_description")] string ErrorDescription
);
