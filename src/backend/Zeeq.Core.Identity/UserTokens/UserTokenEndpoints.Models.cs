using System.ComponentModel.DataAnnotations;

namespace Zeeq.Core.Identity;

/// <summary>
/// Browser request body for minting a long-lived user-owned MCP bearer token.
/// </summary>
/// <param name="DisplayName">User-facing token name shown in management UI.</param>
/// <param name="ExpiresInDays">Requested lifetime in days, bounded by auth settings.</param>
public sealed record UserTokenCreateRequest(
    [property: Required, MaxLength(200)] string DisplayName,
    [property: Range(30, 730)] int? ExpiresInDays
);

/// <summary>
/// Non-sensitive metadata for a long-lived user token.
/// </summary>
/// <remarks>
/// The plaintext bearer token is never stored and is not available after the
/// creation response. This summary is safe for list views because it contains
/// only the local metadata row.
/// </remarks>
/// <param name="Id">Local user-token metadata row ID embedded in issued tokens.</param>
/// <param name="DisplayName">User-facing token name.</param>
/// <param name="CreatedAtUtc">Timestamp when the token metadata row was created.</param>
/// <param name="ExpiresAtUtc">Hard expiry for the issued access token.</param>
/// <param name="RevokedAtUtc">Timestamp when the token was revoked, if any.</param>
/// <param name="LastUsedAtUtc">Most recent successful validation timestamp, if any.</param>
public sealed record UserTokenSummary(
    string Id,
    string DisplayName,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? RevokedAtUtc,
    DateTimeOffset? LastUsedAtUtc
);
