namespace Zeeq.Platform.Membership;

/// <summary>
/// Self-service leave. Rejects if the user is the last owner — the org
/// must have at least one owner at all times.
/// </summary>
public sealed class LeaveOrgHandler(IZeeqMembershipStore store) : IEndpointHandler
{
    /// <summary>
    /// Soft-deletes the membership. Enforces the last-owner guard — an org
    /// must always retain at least one owner.
    /// </summary>
    public async Task<Results<NoContent, NotFound, ValidationProblem>> HandleAsync(
        string orgId,
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        var userId = user.FindFirstValue(OpenIddictConstants.Claims.Subject)!;

        // Check if the caller is a member
        var members = await store.ListMembersForOrganizationAsync(orgId, ct);

        var self = members.FirstOrDefault(m => m.UserId == userId);
        if (self is null)
            return TypedResults.NotFound();

        // Last-owner guard: an org must always have at least one owner
        if (self.Role == "owner" && members.Count(m => m.Role == "owner") <= 1)
        {
            return TypedResults.ValidationProblem(
                new Dictionary<string, string[]>
                {
                    ["org"] = ["The last owner cannot leave the organization."],
                }
            );
        }

        await store.LeaveOrganizationAsync(orgId, userId, ct);

        return TypedResults.NoContent();
    }
}
