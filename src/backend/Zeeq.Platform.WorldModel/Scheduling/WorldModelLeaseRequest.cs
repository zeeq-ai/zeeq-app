using Danom;

namespace Zeeq.Platform.WorldModel.Scheduling;

/// <summary>
/// Lease request metadata supplied by a polling worker.
/// </summary>
/// <remarks>
/// This is the scheduler-facing description of who is claiming work and how long the claim is
/// valid. Runtime hosting will eventually renew leases while long-running processing is active,
/// but the core scheduler only needs the owner and expiry to create leases.
/// </remarks>
public sealed record WorldModelLeaseRequest(string OwnerId, DateTimeOffset ExpiresAtUtc)
{
    /// <summary>
    /// Validates and creates lease request metadata.
    /// </summary>
    /// <param name="ownerId">Stable worker identifier that will own any claimed leases.</param>
    /// <param name="expiresAtUtc">UTC time when claimed leases should become eligible again.</param>
    public static Result<WorldModelLeaseRequest, string> Create(
        string ownerId,
        DateTimeOffset expiresAtUtc
    )
    {
        if (string.IsNullOrWhiteSpace(ownerId))
        {
            return Result<WorldModelLeaseRequest, string>.Error("Lease owner id is required.");
        }

        return Result<WorldModelLeaseRequest, string>.Ok(new(ownerId.Trim(), expiresAtUtc));
    }
}
