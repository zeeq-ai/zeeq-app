namespace Zeeq.Platform.Membership;

/// <summary>
/// Gets one organization for system-admin inspection.
/// </summary>
public sealed class GetSystemOrganizationHandler(ISystemOrganizationManagementStore store)
    : IEndpointHandler
{
    /// <summary>
    /// Returns organization details, or <see cref="NotFound"/> when missing.
    /// </summary>
    public async Task<Results<Ok<SystemOrganizationDetailsResponse>, NotFound>> HandleAsync(
        string orgId,
        CancellationToken ct
    )
    {
        var organization = await store.FindOrganizationAsync(orgId, ct);

        return organization is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(organization.ToResponse());
    }
}
