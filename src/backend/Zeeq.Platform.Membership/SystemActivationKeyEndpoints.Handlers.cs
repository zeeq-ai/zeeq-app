using Zeeq.Core.Common;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;

namespace Zeeq.Platform.Membership;

/// <summary>
/// Lists system activation keys.
/// </summary>
/// <remarks>
/// This handler backs the system-admin activation-key table. It returns
/// metadata only; raw activation key material is never available after the
/// create response because the backend persists only hashes.
/// </remarks>
public sealed class ListSystemActivationKeysHandler(
    IOrganizationActivationKeyStore store,
    AppSettings appSettings
) : IEndpointHandler
{
    /// <summary>
    /// Returns a paged activation-key list.
    /// </summary>
    /// <remarks>
    /// Search and status filtering are intentionally administrative conveniences
    /// for a low-volume key set. The store owns the query details so the HTTP
    /// layer stays focused on request validation and response mapping.
    /// </remarks>
    public async Task<
        Results<Ok<PagedResponse<SystemActivationKeyResponse>>, NotFound>
    > HandleAsync(
        int page,
        int pageSize,
        string? query,
        OrganizationActivationKeyStatus? status,
        CancellationToken ct
    )
    {
        if (!appSettings.Platform.OrganizationActivationKeysEnabled)
        {
            return TypedResults.NotFound();
        }

        var pageResult = await store.ListKeysAsync(page, pageSize, query, status, ct);

        return TypedResults.Ok(pageResult.ToResponse());
    }
}

/// <summary>
/// Creates system activation keys.
/// </summary>
/// <remarks>
/// The raw key is generated server-side and returned only in the create
/// response. The stored record receives a hash of that key, the optional
/// operator note, and the system-admin user id for audit visibility.
/// </remarks>
public sealed class CreateSystemActivationKeyHandler(
    IOrganizationActivationKeyStore store,
    AppSettings appSettings
) : IEndpointHandler
{
    /// <summary>
    /// Creates one key and returns the raw value exactly once.
    /// </summary>
    /// <remarks>
    /// Expiration defaults come from <see cref="AppSettings.Platform"/> so
    /// local and deployed environments can tune key lifetime without changing
    /// endpoint code. Requests cannot exceed the configured maximum lifetime.
    /// </remarks>
    public async Task<
        Results<
            Created<CreateSystemActivationKeyResponse>,
            ValidationProblem,
            ProblemHttpResult,
            NotFound
        >
    > HandleAsync(
        CreateSystemActivationKeyRequest request,
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        if (!appSettings.Platform.OrganizationActivationKeysEnabled)
        {
            return TypedResults.NotFound();
        }

        var adminUserId = user.FindFirstValue(OpenIddictConstants.Claims.Subject);
        if (string.IsNullOrWhiteSpace(adminUserId))
        {
            return TypedResults.Problem(
                title: "Missing subject claim.",
                detail: "The authenticated system admin request did not include a subject claim.",
                statusCode: StatusCodes.Status401Unauthorized
            );
        }

        // Default lifetime is configured centrally; the UI slider is only a
        // client affordance and this server-side bound remains authoritative.
        var lifetimeDays =
            request.ExpiresInDays
            ?? appSettings.Platform.OrganizationActivationKeyDefaultLifetimeDays;
        if (
            lifetimeDays < 1
            || lifetimeDays > appSettings.Platform.OrganizationActivationKeyMaxLifetimeDays
            || lifetimeDays > PlatformSettings.MaxSupportedOrganizationActivationKeyLifetimeDays
        )
        {
            var maxLifetimeDays = Math.Min(
                appSettings.Platform.OrganizationActivationKeyMaxLifetimeDays,
                PlatformSettings.MaxSupportedOrganizationActivationKeyLifetimeDays
            );

            return ValidationProblem(
                "expiresInDays",
                $"Expiration must be between 1 and {maxLifetimeDays} days."
            );
        }

        var now = DateTimeOffset.UtcNow;
        var rawKey = OrganizationActivationKeyMaterial.GenerateKey();
        // Store only the hash. The raw key leaves the backend once in the
        // response below and cannot be recovered later.
        var activationKey = await store.CreateKeyAsync(
            new OrganizationActivationKey
            {
                Id = "oak_" + Guid.NewGuid().ToString("N"),
                KeyHash = OrganizationActivationKeyMaterial.ComputeHash(rawKey),
                Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
                CreatedByUserId = adminUserId,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                ExpiresAtUtc = now.AddDays(lifetimeDays),
            },
            ct
        );

        return TypedResults.Created(
            $"/api/v1/admin/activation-keys/{activationKey.Id}",
            new CreateSystemActivationKeyResponse(
                activationKey.Id,
                rawKey,
                activationKey.Note,
                activationKey.CreatedAtUtc,
                activationKey.ExpiresAtUtc
            )
        );
    }

    private static ValidationProblem ValidationProblem(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}

/// <summary>
/// Revokes unused system activation keys.
/// </summary>
/// <remarks>
/// Revocation is limited to keys that have not been activated. Once a key has
/// been exchanged, the row becomes an audit record linking the key provenance
/// to the organization activation event and is not mutable through this
/// handler.
/// </remarks>
public sealed class RevokeSystemActivationKeyHandler(
    IOrganizationActivationKeyStore store,
    AppSettings appSettings
) : IEndpointHandler
{
    /// <summary>
    /// Revokes an unused key.
    /// </summary>
    /// <remarks>
    /// A missing response covers both nonexistent keys and keys that are no
    /// longer revocable because they were already activated or disabled.
    /// </remarks>
    public async Task<
        Results<Ok<SystemActivationKeyResponse>, NotFound, ProblemHttpResult>
    > HandleAsync(string keyId, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!appSettings.Platform.OrganizationActivationKeysEnabled)
        {
            return TypedResults.NotFound();
        }

        var adminUserId = user.FindFirstValue(OpenIddictConstants.Claims.Subject);
        if (string.IsNullOrWhiteSpace(adminUserId))
        {
            return TypedResults.Problem(
                title: "Missing subject claim.",
                detail: "The authenticated system admin request did not include a subject claim.",
                statusCode: StatusCodes.Status401Unauthorized
            );
        }

        // The store enforces "never activated and not already disabled" in the
        // update predicate so revoke cannot race with a successful exchange.
        var key = await store.RevokeKeyAsync(keyId, ct);
        if (key is null)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(key.ToResponse());
    }
}
