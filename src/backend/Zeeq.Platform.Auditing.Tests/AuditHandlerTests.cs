using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Zeeq.Core.Auditing;
using Zeeq.Core.Documents;
using Zeeq.Core.Identity;

namespace Zeeq.Platform.Auditing.Tests;

/// <summary>
/// Guards read authorization and request mapping for audit history endpoints.
/// </summary>
public sealed class AuditHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RecordingReader : IAuditTrailReader
    {
        public AuditHistoryQuery? LastQuery { get; private set; }
        public string? LastOrganizationId { get; private set; }
        public string? LastTeamId { get; private set; }
        public AuditEventDetail? Detail { get; set; }

        public Task<AuditHistoryPage> ListOrganizationAsync(
            string organizationId,
            string? teamId,
            AuditHistoryQuery query,
            CancellationToken ct
        )
        {
            (LastOrganizationId, LastTeamId, LastQuery) = (organizationId, teamId, query);
            return Task.FromResult(new AuditHistoryPage([], null, AuditCoverage.FirstSlice));
        }

        public Task<AuditEventDetail?> GetOrganizationEventAsync(
            string organizationId,
            string? teamId,
            Guid eventId,
            DateTimeOffset occurredAtUtc,
            CancellationToken ct
        ) => Task.FromResult(Detail);

        public Task<AuditHistoryPage> ListSystemAsync(AuditHistoryQuery query, CancellationToken ct)
        {
            LastQuery = query;
            return Task.FromResult(new AuditHistoryPage([], null, AuditCoverage.FirstSlice));
        }

        public Task<AuditEventDetail?> GetSystemEventAsync(
            Guid eventId,
            DateTimeOffset occurredAtUtc,
            CancellationToken ct
        ) => Task.FromResult(Detail);
    }

    private sealed class FixedAccess(AuditReadScope? scope) : AuditReadAccess(null!, null!)
    {
        public override Task<AuditReadScope?> ResolveAsync(
            ClaimsPrincipal user,
            CancellationToken ct
        ) => Task.FromResult(scope);
    }

    private static AuditEventDetail DetailFor(string? libraryId) =>
        new(
            new AuditEventSummary
            {
                Id = Guid.NewGuid(),
                OccurredAtUtc = Now,
                Scope = AuditScope.Organization,
                EventType = "document.content_updated",
                ChangeType = AuditChangeType.Edited,
                Area = AuditArea.Content,
                SubjectType = "document",
                SubjectId = "d",
                SubjectLabel = "a.md",
                LibraryId = libraryId,
                Actor = AuditActor.Worker("x"),
                OperationId = Guid.NewGuid(),
            },
            null,
            null
        );

    private static readonly ZeeqIdentity Identity = new(
        "user_1",
        "org_1",
        "team_1",
        null,
        null,
        null,
        null,
        null
    );

    [Test]
    public async Task Scope_NoMembership_IsDenied()
    {
        var scope = await AuditReadAccess.BuildScopeAsync(
            Identity,
            null,
            _ => throw new InvalidOperationException(),
            default
        );

        await Assert.That(scope).IsNull();
    }

    [Test]
    [Arguments("owner")]
    [Arguments("admin")]
    public async Task Scope_OwnerAndAdmin_SeeWholeOrganization_WithoutLoadingLibraries(string role)
    {
        var scope = await AuditReadAccess.BuildScopeAsync(
            Identity,
            role,
            _ => throw new InvalidOperationException(),
            default
        );

        await Assert.That(scope!.IsOrgAdmin).IsTrue();
        await Assert.That(scope.LibraryIds).IsNull();
        await Assert.That(scope.TeamId).IsNull();
        await Assert.That(scope.OrganizationId).IsEqualTo("org_1");
    }

    [Test]
    public async Task Scope_Member_IsLimitedToCurrentlyAccessibleLibraries()
    {
        IReadOnlyList<Library> libraries =
        [
            new()
            {
                Id = "lib_shared",
                OrganizationId = "org_1",
                Name = "shared",
            },
            new()
            {
                Id = "lib_mine",
                OrganizationId = "org_1",
                TeamId = "team_1",
                Name = "mine",
            },
            new()
            {
                Id = "lib_other_team",
                OrganizationId = "org_1",
                TeamId = "team_2",
                Name = "other",
            },
        ];

        var scope = await AuditReadAccess.BuildScopeAsync(
            Identity,
            "member",
            _ => Task.FromResult(libraries),
            default
        );

        await Assert.That(scope!.IsOrgAdmin).IsFalse();
        await Assert.That(scope.LibraryIds!).IsEquivalentTo(new[] { "lib_shared", "lib_mine" });
        await Assert.That(scope.TeamId).IsEqualTo("team_1");
    }

    [Test]
    public async Task List_NonMember_Gets404_AndNoQueryRuns()
    {
        var reader = new RecordingReader();
        var handler = new ListAuditHistoryHandler(reader, new FixedAccess(null), new FakeTime());

        var result = await handler.HandleAsync(
            new ClaimsPrincipal(),
            new AuditHistoryRequest(),
            default
        );

        await Assert.That(result.Result is NotFound).IsTrue();
        await Assert.That(reader.LastQuery).IsNull();
    }

    [Test]
    public async Task List_Member_PassesServerDerivedRestrictions_AndDefaultsToLast90Days()
    {
        var reader = new RecordingReader();
        var scope = new AuditReadScope("org_1", "team_1", false, ["lib_a"]);
        var handler = new ListAuditHistoryHandler(reader, new FixedAccess(scope), new FakeTime());

        await handler.HandleAsync(new ClaimsPrincipal(), new AuditHistoryRequest(), default);

        await Assert.That(reader.LastOrganizationId).IsEqualTo("org_1");
        await Assert.That(reader.LastTeamId).IsEqualTo("team_1");
        await Assert
            .That(reader.LastQuery!.RestrictToLibraryIds!)
            .IsEquivalentTo(new[] { "lib_a" });
        await Assert.That(reader.LastQuery.FromUtc).IsEqualTo(Now.AddDays(-90));
        await Assert.That(reader.LastQuery.Limit).IsEqualTo(AuditLimits.DefaultPageSize);
    }

    [Test]
    public async Task List_AllTime_RemovesLowerBound_ButCannotCombineWithFrom()
    {
        var reader = new RecordingReader();
        var scope = new AuditReadScope("org_1", null, true, null);
        var handler = new ListAuditHistoryHandler(reader, new FixedAccess(scope), new FakeTime());

        await handler.HandleAsync(
            new ClaimsPrincipal(),
            new AuditHistoryRequest { AllTime = true },
            default
        );
        await Assert.That(reader.LastQuery!.FromUtc).IsNull();
        await Assert.That(reader.LastQuery.RestrictToLibraryIds).IsNull();

        var conflict = await handler.HandleAsync(
            new ClaimsPrincipal(),
            new AuditHistoryRequest { AllTime = true, FromUtc = Now },
            default
        );
        await Assert.That(conflict.Result is ValidationProblem).IsTrue();
    }

    [Test]
    public async Task Detail_Member_CannotOpenEventOfInaccessibleOrLibraryLessSubject()
    {
        var reader = new RecordingReader();
        var scope = new AuditReadScope("org_1", "team_1", false, ["lib_a"]);
        var handler = new GetAuditEventHandler(reader, new FixedAccess(scope));

        reader.Detail = DetailFor("lib_gone");
        var hidden = await handler.HandleAsync(
            new ClaimsPrincipal(),
            Guid.NewGuid(),
            Now.ToString("O"),
            default
        );
        reader.Detail = DetailFor(null);
        var orgWide = await handler.HandleAsync(
            new ClaimsPrincipal(),
            Guid.NewGuid(),
            Now.ToString("O"),
            default
        );
        reader.Detail = DetailFor("lib_a");
        var allowed = await handler.HandleAsync(
            new ClaimsPrincipal(),
            Guid.NewGuid(),
            Now.ToString("O"),
            default
        );

        await Assert.That(hidden.Result is NotFound).IsTrue();
        await Assert.That(orgWide.Result is NotFound).IsTrue();
        await Assert.That(allowed.Result is Ok<AuditEventDetail>).IsTrue();
    }

    [Test]
    public async Task Detail_Timestamp_KeepsMicrosecondsAndRejectsGarbage()
    {
        var reader = new RecordingReader { Detail = DetailFor(null) };
        var handler = new GetAuditEventHandler(
            reader,
            new FixedAccess(new AuditReadScope("org_1", null, true, null))
        );

        var bad = await handler.HandleAsync(
            new ClaimsPrincipal(),
            Guid.NewGuid(),
            "not-a-date",
            default
        );
        await Assert.That(bad.Result is ValidationProblem).IsTrue();

        await Assert
            .That(AuditProblems.TryParseTimestamp("2026-10-31T12:00:00.123456Z", out var parsed))
            .IsTrue();
        await Assert.That(parsed.Ticks % TimeSpan.TicksPerMillisecond).IsEqualTo(4560L);
        await Assert.That(parsed.Offset).IsEqualTo(TimeSpan.Zero);
    }

    [Test]
    public async Task Detail_Admin_CanOpenDeletedLibraryAndOrgWideEvents()
    {
        var reader = new RecordingReader { Detail = DetailFor(null) };
        var handler = new GetAuditEventHandler(
            reader,
            new FixedAccess(new AuditReadScope("org_1", null, true, null))
        );

        var result = await handler.HandleAsync(
            new ClaimsPrincipal(),
            Guid.NewGuid(),
            Now.ToString("O"),
            default
        );

        await Assert.That(result.Result is Ok<AuditEventDetail>).IsTrue();
    }

    [Test]
    public async Task SystemList_BadRange_ReturnsValidationProblem()
    {
        var handler = new ListSystemAuditHistoryHandler(new RecordingReader(), new FakeTime());

        var result = await handler.HandleAsync(
            new AuditHistoryRequest { FromUtc = Now, ToUtc = Now.AddDays(-1) },
            default
        );

        await Assert.That(result.Result is ValidationProblem).IsTrue();
    }
}
