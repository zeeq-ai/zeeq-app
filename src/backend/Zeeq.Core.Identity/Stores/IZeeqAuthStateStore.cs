namespace Zeeq.Core.Identity;

/// <summary>
/// Transient key–value store for short-lived OAuth flow state that must survive
/// a browser redirect round-trip.
/// </summary>
/// <remarks>
/// Used to hold CSRF state tokens, PKCE code verifiers, and any other one-time
/// payloads that are written before a redirect and read once on callback.
/// The <c>purpose</c> + <c>key</c> pair is the composite lookup key: <c>purpose</c>
/// namespaces the key space so that different OAuth flows cannot accidentally
/// collide (e.g. <c>"oauth_state"</c> vs <c>"provider_callback"</c>).
/// <para>
/// <see cref="ConsumeAsync"/> must be atomic: it reads the payload and deletes the
/// row in the same operation. This ensures that state can only be consumed once,
/// preventing replay attacks against the callback endpoint.
/// </para>
/// <para>
/// <strong>Current limitation:</strong> the existing implementation is process-local
/// and will not survive a process restart or work correctly in a multi-node deployment.
/// Production deployments must back this store with a distributed cache (e.g. Redis)
/// or a database table with TTL-based expiry.
/// </para>
/// </remarks>
public interface IZeeqAuthStateStore
{
    /// <summary>
    /// Stores a JSON payload under the given <paramref name="purpose"/> and
    /// <paramref name="key"/>, expiring at <paramref name="expiresAtUtc"/>.
    /// </summary>
    /// <param name="purpose">Logical namespace for the key, e.g. <c>"oauth_state"</c>.
    /// Must be consistent between the <see cref="StoreAsync"/> and the matching
    /// <see cref="ConsumeAsync"/> call.</param>
    /// <param name="key">Opaque key that will be carried through the redirect, typically
    /// the <c>state</c> parameter in an OAuth authorization request. Should be a
    /// cryptographically random value.</param>
    /// <param name="payloadJson">Serialised payload to store. Callers must not include
    /// secrets (tokens, passwords) in this payload as the store may not be encrypted at
    /// rest.</param>
    /// <param name="expiresAtUtc">Hard expiry for the stored entry. Implementations must
    /// treat expired entries as absent; they must not be returned by
    /// <see cref="ConsumeAsync"/>.</param>
    /// <param name="cancellationToken"/>
    Task StoreAsync(
        string purpose,
        string key,
        string payloadJson,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Atomically reads and deletes the payload stored under the given
    /// <paramref name="purpose"/> and <paramref name="key"/>.
    /// </summary>
    /// <returns>
    /// The JSON payload if a non-expired entry was found; <see langword="null"/> if no
    /// entry exists, the entry has expired, or it has already been consumed.
    /// </returns>
    /// <remarks>
    /// Callers must treat a <see langword="null"/> return as an invalid or replayed
    /// request and reject it immediately. The implementation must guarantee that two
    /// concurrent calls for the same key return a non-null value at most once.
    /// </remarks>
    Task<string?> ConsumeAsync(string purpose, string key, CancellationToken cancellationToken);
}
