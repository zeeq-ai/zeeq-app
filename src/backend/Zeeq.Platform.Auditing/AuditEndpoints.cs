using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Zeeq.Core.Auditing;
using Zeeq.Core.Identity;

namespace Zeeq.Platform.Auditing;

/// <summary>
/// Tenant audit history: a metadata-only feed and lazily loaded event detail.
/// </summary>
/// <remarks>
/// The organization comes from the caller's claims, never from a route or query value. Read
/// authorization is evaluated against current membership and library access on every request.
/// Feeds default to the last 90 days; <c>allTime=true</c> explicitly removes the lower bound.
/// </remarks>
public sealed class AuditEndpoints : IEndpoint
{
    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder app, IEndpointRouteBuilder rootApp)
    {
        var group = app.MapGroup("audit/history")
            .WithTags("AuditHistory")
            .RequireAuthorization(
                new AuthorizeAttribute
                {
                    AuthenticationSchemes = SetupIdentityExtension.CookieScheme,
                }
            );

        // GET /api/v1/audit/history
        group
            .MapGet(
                "/",
                static (
                    ClaimsPrincipal user,
                    [AsParameters] AuditHistoryRequest request,
                    [FromServices] ListAuditHistoryHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(user, request, ct)
            )
            .WithName("ListAuditHistory")
            .WithSummary("List metadata-only audit history entries for the active organization.");

        // GET /api/v1/audit/history/{eventId}
        group
            .MapGet(
                "/{eventId:guid}",
                static (
                    Guid eventId,
                    [FromQuery, MaxLength(64)] string occurredAtUtc,
                    ClaimsPrincipal user,
                    [FromServices] GetAuditEventHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(user, eventId, occurredAtUtc, ct)
            )
            .WithName("GetAuditHistoryEvent")
            .WithSummary("Get one audit event with its before and after snapshots.");
    }
}

/// <summary>System-administrator audit history across system events and sysadmin-actor tenant changes.</summary>
public sealed class AuditAdminEndpoints : ISystemAdminEndpoint
{
    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder app, IEndpointRouteBuilder rootApp)
    {
        var group = app.MapGroup("audit/history").WithTags("AuditHistory");

        // GET /api/v1/admin/audit/history
        group
            .MapGet(
                "/",
                static (
                    [AsParameters] AuditHistoryRequest request,
                    [FromServices] ListSystemAuditHistoryHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(request, ct)
            )
            .WithName("ListSystemAuditHistory")
            .WithSummary("List metadata-only system audit history entries.");

        // GET /api/v1/admin/audit/history/{eventId}
        group
            .MapGet(
                "/{eventId:guid}",
                static (
                    Guid eventId,
                    [FromQuery, MaxLength(64)] string occurredAtUtc,
                    [FromServices] GetSystemAuditEventHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(eventId, occurredAtUtc, ct)
            )
            .WithName("GetSystemAuditHistoryEvent")
            .WithSummary("Get one system audit event with its before and after snapshots.");
    }
}

/// <summary>Filter, cursor, and range inputs shared by the tenant and system feeds.</summary>
public sealed record AuditHistoryRequest
{
    /// <summary>Restrict to one coarse change type.</summary>
    [FromQuery]
    public AuditChangeType? ChangeType { get; init; }

    /// <summary>Restrict to one product area.</summary>
    [FromQuery]
    public AuditArea? Area { get; init; }

    /// <summary>Restrict to one actor id (user, credential, or service).</summary>
    [FromQuery, MaxLength(AuditLimits.MaxIdentifierLength)]
    public string? ActorId { get; init; }

    /// <summary>Restrict to one subject type, for example <c>document</c>.</summary>
    [FromQuery, MaxLength(64)]
    public string? SubjectType { get; init; }

    /// <summary>Restrict to one subject id; combine with <c>subjectType</c>.</summary>
    [FromQuery, MaxLength(AuditLimits.MaxIdentifierLength)]
    public string? SubjectId { get; init; }

    /// <summary>Restrict to events of one library (parent of document subjects).</summary>
    [FromQuery, MaxLength(128)]
    public string? LibraryId { get; init; }

    /// <summary>Restrict to the events written by one operation.</summary>
    [FromQuery]
    public Guid? OperationId { get; init; }

    /// <summary>Inclusive lower time bound; defaults to 90 days ago unless <c>allTime</c> is set.</summary>
    [FromQuery]
    public DateTimeOffset? FromUtc { get; init; }

    /// <summary>Inclusive upper time bound.</summary>
    [FromQuery]
    public DateTimeOffset? ToUtc { get; init; }

    /// <summary>Removes the default last-90-days lower bound. Cannot be combined with <c>fromUtc</c>.</summary>
    [FromQuery]
    public bool AllTime { get; init; }

    /// <summary>Opaque continuation cursor from a previous page.</summary>
    [FromQuery, MaxLength(256)]
    public string? Cursor { get; init; }

    /// <summary>Page size, 1 to 200 (default 50).</summary>
    [FromQuery, Range(1, AuditLimits.MaxPageSize)]
    public int Limit { get; init; } = AuditLimits.DefaultPageSize;
}

internal static class AuditHistoryRequestMapping
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(90);

    extension(AuditHistoryRequest request)
    {
        /// <summary>Builds the storage query; returns an error text for contradictory inputs.</summary>
        public (AuditHistoryQuery? Query, string? Error) ToQuery(
            DateTimeOffset now,
            IReadOnlyCollection<string>? restrictToLibraryIds
        )
        {
            if (request.AllTime && request.FromUtc is not null)
            {
                return (null, "allTime cannot be combined with fromUtc.");
            }

            if (request is { FromUtc: { } from, ToUtc: { } to } && from > to)
            {
                return (null, "fromUtc must not be after toUtc.");
            }

            return (
                new AuditHistoryQuery
                {
                    FromUtc = request.AllTime ? null : request.FromUtc ?? now - DefaultWindow,
                    ToUtc = request.ToUtc,
                    Cursor = request.Cursor,
                    Limit = request.Limit,
                    ChangeType = request.ChangeType,
                    Area = request.Area,
                    ActorId = request.ActorId,
                    SubjectType = request.SubjectType,
                    SubjectId = request.SubjectId,
                    LibraryId = request.LibraryId,
                    OperationId = request.OperationId,
                    RestrictToLibraryIds = restrictToLibraryIds,
                },
                null
            );
        }
    }
}
