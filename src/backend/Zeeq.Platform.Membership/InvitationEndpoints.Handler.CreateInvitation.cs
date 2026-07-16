using Zeeq.Core.Models;

namespace Zeeq.Platform.Membership;

/// <summary>
/// Creates a pending membership row with <see cref="MembershipStatus.Pending"/>
/// and a 7-day expiry. Only <c>admin</c> and <c>member</c> roles can be
/// assigned via invitation (owners must be promoted after acceptance).
/// </summary>
public sealed class CreateInvitationHandler(IZeeqMembershipStore store) : IEndpointHandler
{
    /// <summary>
    /// Validates the request and creates a pending membership row with
    /// a 7-day expiry. Owners cannot be assigned via invitation.
    /// </summary>
    public async Task<Results<Created<InvitationResponse>, ValidationProblem>> HandleAsync(
        string orgId,
        CreateInvitationRequest request,
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        // Validate email
        var email = request.Email?.Trim();
        if (string.IsNullOrWhiteSpace(email))
        {
            return TypedResults.ValidationProblem(
                new Dictionary<string, string[]> { ["email"] = ["Email is required."] }
            );
        }

        // Only admin and member roles can be assigned via invitation
        if (request.Role is not ("admin" or "member"))
        {
            return TypedResults.ValidationProblem(
                new Dictionary<string, string[]>
                {
                    ["role"] = ["Role must be 'admin' or 'member'."],
                }
            );
        }

        // Reject duplicate invitations for the same org/email pair
        var existing = await store.ListPendingInvitationsForEmailAsync(email, ct);

        if (existing.Any(i => i.OrganizationId == orgId))
        {
            return TypedResults.ValidationProblem(
                new Dictionary<string, string[]>
                {
                    ["email"] = ["An active invitation already exists for this organization."],
                }
            );
        }

        // Create a pending membership row with a 7-day expiry
        var invitedByUserId = user.FindFirstValue(OpenIddictConstants.Claims.Subject)!;

        var invitation = await store.CreateInvitationAsync(
            new OrganizationMembership
            {
                Id = "inv_" + Guid.NewGuid().ToString("N"),
                OrganizationId = orgId,
                UserId = null,
                Role = request.Role,
                Status = MembershipStatus.Pending,
                InvitedEmail = email,
                CreatedByUserId = invitedByUserId,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(7),
            },
            ct
        );

        var org = await store.FindOrganizationByIdAsync(orgId, ct);

        return TypedResults.Created(
            $"/api/v1/orgs/{orgId}/invitations/{invitation.Id}",
            new InvitationResponse(
                invitation.Id,
                orgId,
                org?.DisplayName,
                invitation.InvitedEmail,
                invitation.Role,
                invitation.CreatedAtUtc,
                invitation.ExpiresAtUtc!.Value
            )
        );
    }
}
