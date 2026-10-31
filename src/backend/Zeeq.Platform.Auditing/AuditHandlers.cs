using System.Globalization;
using Zeeq.Core.Auditing;

namespace Zeeq.Platform.Auditing;

/// <summary>Lists the active organization's metadata-only audit entries.</summary>
public sealed class ListAuditHistoryHandler(
    IAuditTrailReader reader,
    AuditReadAccess access,
    TimeProvider time
) : IEndpointHandler
{
    /// <summary>Handles the request; unauthorized callers get 404 so history is not advertised.</summary>
    public async Task<Results<Ok<AuditHistoryPage>, NotFound, ValidationProblem>> HandleAsync(
        ClaimsPrincipal user,
        AuditHistoryRequest request,
        CancellationToken ct
    )
    {
        if (await access.ResolveAsync(user, ct) is not { } scope)
        {
            return TypedResults.NotFound();
        }

        var (query, error) = request.ToQuery(time.GetUtcNow(), scope.LibraryIds);

        if (query is null)
        {
            return AuditProblems.Invalid(error!);
        }

        try
        {
            return TypedResults.Ok(
                await reader.ListOrganizationAsync(scope.OrganizationId, scope.TeamId, query, ct)
            );
        }
        catch (ArgumentException ex)
        {
            return AuditProblems.Invalid(ex.Message);
        }
    }
}

/// <summary>Loads one tenant audit event including snapshots.</summary>
public sealed class GetAuditEventHandler(IAuditTrailReader reader, AuditReadAccess access)
    : IEndpointHandler
{
    /// <summary>Handles the request; out-of-scope events are indistinguishable from missing ones.</summary>
    public async Task<Results<Ok<AuditEventDetail>, NotFound, ValidationProblem>> HandleAsync(
        ClaimsPrincipal user,
        Guid eventId,
        string occurredAtUtc,
        CancellationToken ct
    )
    {
        if (await access.ResolveAsync(user, ct) is not { } scope)
        {
            return TypedResults.NotFound();
        }

        if (!AuditProblems.TryParseTimestamp(occurredAtUtc, out var occurredAt))
        {
            return AuditProblems.Invalid("occurredAtUtc must be an ISO-8601 timestamp.");
        }

        var detail = await reader.GetOrganizationEventAsync(
            scope.OrganizationId,
            scope.TeamId,
            eventId,
            occurredAt,
            ct
        );

        // Non-admins may only open events of libraries they can currently access.
        if (
            detail is null
            || (
                !scope.IsOrgAdmin
                && (
                    detail.Event.LibraryId is null
                    || scope.LibraryIds?.Contains(detail.Event.LibraryId) != true
                )
            )
        )
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(detail);
    }
}

/// <summary>Lists system-admin audit entries. Authorization is the admin route group.</summary>
public sealed class ListSystemAuditHistoryHandler(IAuditTrailReader reader, TimeProvider time)
    : IEndpointHandler
{
    /// <summary>Handles the request.</summary>
    public async Task<Results<Ok<AuditHistoryPage>, ValidationProblem>> HandleAsync(
        AuditHistoryRequest request,
        CancellationToken ct
    )
    {
        var (query, error) = request.ToQuery(time.GetUtcNow(), restrictToLibraryIds: null);

        if (query is null)
        {
            return AuditProblems.Invalid(error!);
        }

        try
        {
            return TypedResults.Ok(await reader.ListSystemAsync(query, ct));
        }
        catch (ArgumentException ex)
        {
            return AuditProblems.Invalid(ex.Message);
        }
    }
}

/// <summary>Loads one system audit event including snapshots.</summary>
public sealed class GetSystemAuditEventHandler(IAuditTrailReader reader) : IEndpointHandler
{
    /// <summary>Handles the request.</summary>
    public async Task<Results<Ok<AuditEventDetail>, NotFound, ValidationProblem>> HandleAsync(
        Guid eventId,
        string occurredAtUtc,
        CancellationToken ct
    )
    {
        if (!AuditProblems.TryParseTimestamp(occurredAtUtc, out var occurredAt))
        {
            return AuditProblems.Invalid("occurredAtUtc must be an ISO-8601 timestamp.");
        }

        return await reader.GetSystemEventAsync(eventId, occurredAt, ct) is { } detail
            ? TypedResults.Ok(detail)
            : TypedResults.NotFound();
    }
}

internal static class AuditProblems
{
    /// <summary>
    /// Parses the exact event timestamp from the feed. Kept as a string at the API boundary so
    /// microsecond precision is not lost to client-side millisecond dates.
    /// </summary>
    public static bool TryParseTimestamp(string? value, out DateTimeOffset result) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out result
        );

    public static ValidationProblem Invalid(string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["query"] = [message] });
}
