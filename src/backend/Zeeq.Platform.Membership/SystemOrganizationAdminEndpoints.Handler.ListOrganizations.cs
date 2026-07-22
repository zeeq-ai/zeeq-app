namespace Zeeq.Platform.Membership;

/// <summary>
/// Lists organizations for the system-admin organization management table.
/// </summary>
public sealed class ListSystemOrganizationsHandler(ISystemOrganizationManagementStore store)
    : IEndpointHandler
{
    /// <summary>
    /// Returns a server-paginated organization page using backend matching.
    /// </summary>
    public async Task<Ok<PagedResponse<SystemOrganizationSummaryResponse>>> HandleAsync(
        int page,
        int pageSize,
        string? query,
        CancellationToken ct
    )
    {
        var organizations = await store.ListOrganizationsAsync(page, pageSize, query, ct);

        return TypedResults.Ok(organizations.ToResponse());
    }
}
