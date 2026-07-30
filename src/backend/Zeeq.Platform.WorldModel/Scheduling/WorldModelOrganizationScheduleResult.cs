namespace Zeeq.Platform.WorldModel.Scheduling;

/// <summary>
/// Result of one atomic organization scheduling transition.
/// </summary>
/// <remarks>
/// This groups the claimed leases with the persisted organization queue state that produced them.
/// It is intentionally returned from the store rather than assembled by the scheduler so a Postgres
/// implementation can make lease creation and deficit persistence one transaction.
/// </remarks>
public sealed record WorldModelOrganizationScheduleResult(
    WorldModelOrganizationQueueState State,
    IReadOnlyList<WorldModelWorkLease> Leases
);
