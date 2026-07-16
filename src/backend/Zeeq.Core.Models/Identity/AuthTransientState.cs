namespace Zeeq.Core.Models;

/// <summary>
/// Short-lived, single-use state record for OAuth PKCE flows, browser handoff
/// tickets, and user-token grant tickets.
/// </summary>
/// <remarks>
/// Each row is consumed exactly once by opaque key, then marked with
/// <see cref="ConsumedAtUtc"/>. Expired rows are eligible for cleanup.
/// The <see cref="Purpose"/> discriminator separates the three use cases
/// (OAuth state, browser handoff, token grant) inside a single store.
/// Backed by the <c>auth_transient_states</c> table.
/// </remarks>
public sealed class AuthTransientState
{
    /// <summary>
    /// Opaque consume-once key (URL-safe random value).
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// Discriminator: <c>"oauth_state"</c>, <c>"browser_handoff"</c>, or
    /// <c>"token_grant"</c>.
    /// </summary>
    public required string Purpose { get; init; }

    /// <summary>
    /// Serialized payload (PKCE verifier, claims principal, token ID, etc.).
    /// </summary>
    public required string PayloadJson { get; set; }

    /// <summary>
    /// Absolute expiry; rows past this time are dead even if never consumed.
    /// </summary>
    public required DateTimeOffset ExpiresAtUtc { get; set; }

    /// <summary>
    /// Set on first successful consumption; <see langword="null"/> means
    /// the row is still valid and available.
    /// </summary>
    public DateTimeOffset? ConsumedAtUtc { get; set; }
}
