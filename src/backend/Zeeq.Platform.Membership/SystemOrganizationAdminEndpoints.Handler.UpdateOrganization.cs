using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Zeeq.Core.Auditing;
using Zeeq.Core.Models;

namespace Zeeq.Platform.Membership;

/// <summary>
/// Applies system-admin activation and tier changes to an organization.
/// </summary>
/// <remarks>
/// The store records one organization audit fact atomically with the change. When
/// <paramref name="actors"/> is available the fact is attributed to a <c>system_admin</c> actor
/// (the ambient identity and credential details are kept; the channel is the system console); otherwise the store uses its ambient actor.
/// </remarks>
public sealed partial class UpdateSystemOrganizationHandler(
    ISystemOrganizationManagementStore store,
    ILogger<UpdateSystemOrganizationHandler> log,
    IAuditActorAccessor? actors = null
) : IEndpointHandler
{
    private static readonly ActivitySource ActivitySource = new(
        "Zeeq.Platform.Membership.SystemOrganizations"
    );

    /// <summary>
    /// Validates the requested update, logs the mutation, and returns fresh organization details.
    /// </summary>
    public async Task<
        Results<
            Ok<SystemOrganizationDetailsResponse>,
            NotFound,
            ValidationProblem,
            ProblemHttpResult
        >
    > HandleAsync(
        string orgId,
        UpdateSystemOrganizationRequest request,
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        if (request.Active is null && string.IsNullOrWhiteSpace(request.Tier))
        {
            return ValidationProblem("request", "Specify active or tier to update.");
        }

        var tierParse = ParseTier(request.Tier);
        if (tierParse.Validated is false)
        {
            return ValidationProblem("tier", "Tier must be one of Default, Priority, or Low.");
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

        using var activity = ActivitySource.StartActivity("system_org.update");
        activity?.SetTag("zeeq.admin.operation", "system_org.update");
        activity?.SetTag("organization.id", orgId);
        activity?.SetTag("admin.user_id", adminUserId);
        activity?.SetTag("system_org.active_requested", request.Active);
        activity?.SetTag("system_org.tier_requested", request.Tier);

        LogUpdateRequested(log, adminUserId, orgId, request.Active, request.Tier);

        var organization = await store.UpdateOrganizationAdminStateAsync(
            orgId,
            request.Active,
            tierParse.Tier,
            actors?.Current is { } actor
                ? actor with
                {
                    Kind = AuditActorKinds.SystemAdmin,
                    Channel = AuditChannels.SystemConsole,
                }
                : null,
            ct
        );

        if (organization is null)
        {
            LogUpdateNotFound(log, adminUserId, orgId);
            return TypedResults.NotFound();
        }

        var resultingActive =
            organization.ActivatedAtUtc is not null && organization.DisabledAtUtc is null;
        activity?.AddEvent(
            new ActivityEvent(
                "system_org.updated",
                tags: new ActivityTagsCollection
                {
                    ["system_org.active"] = resultingActive,
                    ["system_org.tier"] = organization.Tier.ToString(),
                }
            )
        );
        LogUpdateSucceeded(log, adminUserId, orgId, resultingActive, organization.Tier.ToString());

        return TypedResults.Ok(organization.ToResponse());
    }

    private static (bool Validated, OrganizationTier? Tier) ParseTier(string? tier)
    {
        if (string.IsNullOrWhiteSpace(tier))
        {
            return (true, null);
        }

        var value = tier.Trim();
        return
            Enum.GetNames<OrganizationTier>()
                .Any(name => string.Equals(name, value, StringComparison.OrdinalIgnoreCase))
            && Enum.TryParse(value, ignoreCase: true, out OrganizationTier parsed)
            ? (true, parsed)
            : (false, null);
    }

    private static ValidationProblem ValidationProblem(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    [LoggerMessage(
        EventId = 7100,
        Level = LogLevel.Information,
        Message = "System admin {AdminUserId} requested organization update for {OrganizationId}. Active: {Active}; Tier: {Tier}."
    )]
    private static partial void LogUpdateRequested(
        ILogger logger,
        string adminUserId,
        string organizationId,
        bool? active,
        string? tier
    );

    [LoggerMessage(
        EventId = 7101,
        Level = LogLevel.Information,
        Message = "System admin {AdminUserId} updated organization {OrganizationId}. Active: {Active}; Tier: {Tier}."
    )]
    private static partial void LogUpdateSucceeded(
        ILogger logger,
        string adminUserId,
        string organizationId,
        bool active,
        string tier
    );

    [LoggerMessage(
        EventId = 7102,
        Level = LogLevel.Warning,
        Message = "System admin {AdminUserId} attempted to update missing organization {OrganizationId}."
    )]
    private static partial void LogUpdateNotFound(
        ILogger logger,
        string adminUserId,
        string organizationId
    );
}
