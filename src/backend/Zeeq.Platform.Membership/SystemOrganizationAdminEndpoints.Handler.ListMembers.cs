namespace Zeeq.Platform.Membership;

/// <summary>
/// Lists active members for one organization in the system-admin detail view.
/// </summary>
public sealed class ListSystemOrganizationMembersHandler(ISystemOrganizationManagementStore store)
    : IEndpointHandler
{
    /// <summary>
    /// Returns a server-paginated active member page.
    /// </summary>
    public async Task<Ok<PagedResponse<SystemOrganizationMemberResponse>>> HandleAsync(
        string orgId,
        int page,
        int pageSize,
        CancellationToken ct
    )
    {
        var members = await store.ListMembersAsync(orgId, page, pageSize, ct);

        return TypedResults.Ok(members.ToResponse());
    }
}
