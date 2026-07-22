using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;

namespace Zeeq.Platform.Membership.Tests;

/// <summary>
/// Handler unit tests for the system organization admin endpoint slice.
///
/// Run this test class:
/// dotnet run --project src/backend/Zeeq.Platform.Membership.Tests --output detailed --disable-logo --treenode-filter "/*/*/SystemOrganizationAdminEndpointHandlerTests/*"
/// </summary>
public sealed class SystemOrganizationAdminEndpointHandlerTests
{
    [Test]
    public async Task ListSystemOrganizationsHandler_ReturnsPagedResponse()
    {
        var store = new TestSystemOrganizationManagementStore();
        store.OrganizationPages.Enqueue(
            new SystemOrganizationPage<SystemOrganizationSummary>(
                [TestOrganizationSummary("org_1")],
                Page: 2,
                PageSize: 25,
                TotalCount: 51
            )
        );
        var handler = new ListSystemOrganizationsHandler(store);

        var result = await handler.HandleAsync(2, 25, "acme", CancellationToken.None);

        await Assert.That(result.Value!.Page).IsEqualTo(2);
        await Assert.That(result.Value.TotalCount).IsEqualTo(51);
        await Assert.That(result.Value.Items.Single().Id).IsEqualTo("org_1");
        await Assert.That(store.LastListOrganizationsQuery).IsEqualTo("acme");
    }

    [Test]
    public async Task GetSystemOrganizationHandler_Missing_ReturnsNotFound()
    {
        var handler = new GetSystemOrganizationHandler(new TestSystemOrganizationManagementStore());

        var result = await handler.HandleAsync("org_missing", CancellationToken.None);

        await Assert.That(result.Result is NotFound).IsTrue();
    }

    [Test]
    public async Task ListSystemOrganizationMembersHandler_ReturnsPagedMembers()
    {
        var store = new TestSystemOrganizationManagementStore();
        store.MemberPages["org_1"] = new SystemOrganizationPage<SystemOrganizationMember>(
            [
                new SystemOrganizationMember(
                    "user_1",
                    "Member",
                    "member@example.com",
                    null,
                    "member",
                    DateTimeOffset.UtcNow
                ),
            ],
            Page: 1,
            PageSize: 25,
            TotalCount: 1
        );
        var handler = new ListSystemOrganizationMembersHandler(store);

        var result = await handler.HandleAsync("org_1", 1, 25, CancellationToken.None);

        await Assert.That(result.Value!.Items.Single().Email).IsEqualTo("member@example.com");
        await Assert.That(store.LastListMembersOrgId).IsEqualTo("org_1");
    }

    [Test]
    public async Task UpdateSystemOrganizationHandler_EmptyRequest_ReturnsValidationProblem()
    {
        var handler = new UpdateSystemOrganizationHandler(
            new TestSystemOrganizationManagementStore(),
            NullLogger<UpdateSystemOrganizationHandler>.Instance
        );

        var result = await handler.HandleAsync(
            "org_1",
            new UpdateSystemOrganizationRequest(),
            MembershipTestClaims.TestUser("admin_1"),
            CancellationToken.None
        );

        await Assert.That(result.Result is ValidationProblem).IsTrue();
    }

    [Test]
    public async Task UpdateSystemOrganizationHandler_InvalidTier_ReturnsValidationProblem()
    {
        var handler = new UpdateSystemOrganizationHandler(
            new TestSystemOrganizationManagementStore(),
            NullLogger<UpdateSystemOrganizationHandler>.Instance
        );

        var result = await handler.HandleAsync(
            "org_1",
            new UpdateSystemOrganizationRequest { Tier = "Enterprise" },
            MembershipTestClaims.TestUser("admin_1"),
            CancellationToken.None
        );

        await Assert.That(result.Result is ValidationProblem).IsTrue();
    }

    [Test]
    public async Task UpdateSystemOrganizationHandler_NumericTier_ReturnsValidationProblem()
    {
        var handler = new UpdateSystemOrganizationHandler(
            new TestSystemOrganizationManagementStore(),
            NullLogger<UpdateSystemOrganizationHandler>.Instance
        );

        var result = await handler.HandleAsync(
            "org_1",
            new UpdateSystemOrganizationRequest { Tier = "1" },
            MembershipTestClaims.TestUser("admin_1"),
            CancellationToken.None
        );

        await Assert.That(result.Result is ValidationProblem).IsTrue();
    }

    [Test]
    public async Task UpdateSystemOrganizationHandler_MissingSubject_ReturnsProblem()
    {
        var store = new TestSystemOrganizationManagementStore();
        var handler = new UpdateSystemOrganizationHandler(
            store,
            NullLogger<UpdateSystemOrganizationHandler>.Instance
        );

        var result = await handler.HandleAsync(
            "org_1",
            new UpdateSystemOrganizationRequest { Active = true },
            new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "test")),
            CancellationToken.None
        );

        await Assert.That(result.Result is ProblemHttpResult).IsTrue();
        await Assert.That(store.LastUpdateActive).IsNull();
        await Assert.That(store.LastUpdateTier).IsNull();
    }

    [Test]
    public async Task UpdateSystemOrganizationHandler_ValidRequest_UpdatesStoreAndReturnsDetails()
    {
        var store = new TestSystemOrganizationManagementStore();
        store.Details["org_1"] = TestOrganizationDetails("org_1") with
        {
            ActivatedAtUtc = DateTimeOffset.UtcNow,
            Tier = OrganizationTier.Priority,
        };
        var handler = new UpdateSystemOrganizationHandler(
            store,
            NullLogger<UpdateSystemOrganizationHandler>.Instance
        );

        var result = await handler.HandleAsync(
            "org_1",
            new UpdateSystemOrganizationRequest { Active = true, Tier = "Priority" },
            MembershipTestClaims.TestUser("admin_1"),
            CancellationToken.None
        );
        var ok = result.Result as Ok<SystemOrganizationDetailsResponse>;

        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value!.Tier).IsEqualTo("Priority");
        await Assert.That(store.LastUpdateActive).IsTrue();
        await Assert.That(store.LastUpdateTier).IsEqualTo(OrganizationTier.Priority);
    }

    [Test]
    public async Task UpdateSystemOrganizationHandler_ActiveOnly_LeavesTierUnchanged()
    {
        var store = new TestSystemOrganizationManagementStore();
        store.Details["org_1"] = TestOrganizationDetails("org_1") with
        {
            ActivatedAtUtc = DateTimeOffset.UtcNow,
        };
        var handler = new UpdateSystemOrganizationHandler(
            store,
            NullLogger<UpdateSystemOrganizationHandler>.Instance
        );

        var result = await handler.HandleAsync(
            "org_1",
            new UpdateSystemOrganizationRequest { Active = true },
            MembershipTestClaims.TestUser("admin_1"),
            CancellationToken.None
        );

        await Assert.That(result.Result is Ok<SystemOrganizationDetailsResponse>).IsTrue();
        await Assert.That(store.LastUpdateActive).IsTrue();
        await Assert.That(store.LastUpdateTier).IsNull();
    }

    [Test]
    public async Task UpdateSystemOrganizationHandler_LowercaseTier_ParsesEnum()
    {
        var store = new TestSystemOrganizationManagementStore();
        store.Details["org_1"] = TestOrganizationDetails("org_1") with
        {
            Tier = OrganizationTier.Low,
        };
        var handler = new UpdateSystemOrganizationHandler(
            store,
            NullLogger<UpdateSystemOrganizationHandler>.Instance
        );

        var result = await handler.HandleAsync(
            "org_1",
            new UpdateSystemOrganizationRequest { Tier = "low" },
            MembershipTestClaims.TestUser("admin_1"),
            CancellationToken.None
        );

        await Assert.That(result.Result is Ok<SystemOrganizationDetailsResponse>).IsTrue();
        await Assert.That(store.LastUpdateTier).IsEqualTo(OrganizationTier.Low);
    }

    [Test]
    public async Task GetSystemOrganizationHandler_WithManagedKey_MarksUsesManagedKey()
    {
        var store = new TestSystemOrganizationManagementStore();
        store.Details["org_1"] = TestOrganizationDetails("org_1") with
        {
            LlmConfiguration = new SystemOrganizationLlmConfiguration(
                new SystemOrganizationLlmTier(
                    "Fast",
                    "OpenAI",
                    "gpt-5-mini",
                    null,
                    UsesManagedKey: true,
                    KeyId: "key_1"
                ),
                new SystemOrganizationLlmTier(
                    "High",
                    "Fireworks",
                    "accounts/fireworks/models/deepseek-v4-pro",
                    null,
                    UsesManagedKey: false,
                    KeyId: null
                ),
                new SystemOrganizationLlmTier(
                    "Max",
                    "Fireworks",
                    "accounts/fireworks/models/glm-5p2",
                    null,
                    UsesManagedKey: false,
                    KeyId: null
                )
            ),
        };
        var handler = new GetSystemOrganizationHandler(store);

        var result = await handler.HandleAsync("org_1", CancellationToken.None);
        var ok = result.Result as Ok<SystemOrganizationDetailsResponse>;

        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value!.LlmConfiguration.Fast.UsesManagedKey).IsTrue();
        await Assert.That(ok.Value.LlmConfiguration.Fast.KeyId).IsEqualTo("key_1");
        await Assert.That(ok.Value.LlmConfiguration.High.UsesManagedKey).IsFalse();
    }

    private static SystemOrganizationSummary TestOrganizationSummary(string orgId) =>
        new(
            orgId,
            "Acme",
            "acme",
            null,
            new SystemOrganizationCreator("user_owner", "Owner", "owner@example.com", null),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            MemberCount: 3,
            OrganizationTier.Default,
            TestLlmConfiguration()
        );

    private static SystemOrganizationDetails TestOrganizationDetails(string orgId) =>
        new(
            orgId,
            "Acme",
            "acme",
            null,
            new SystemOrganizationCreator("user_owner", "Owner", "owner@example.com", null),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            MemberCount: 3,
            OrganizationTier.Default,
            TestLlmConfiguration()
        );

    private static SystemOrganizationLlmConfiguration TestLlmConfiguration() =>
        new(
            new SystemOrganizationLlmTier(
                "Fast",
                "Fireworks",
                "accounts/fireworks/models/deepseek-v4-flash",
                null,
                UsesManagedKey: false,
                KeyId: null
            ),
            new SystemOrganizationLlmTier(
                "High",
                "Fireworks",
                "accounts/fireworks/models/deepseek-v4-pro",
                null,
                UsesManagedKey: false,
                KeyId: null
            ),
            new SystemOrganizationLlmTier(
                "Max",
                "Fireworks",
                "accounts/fireworks/models/glm-5p2",
                null,
                UsesManagedKey: false,
                KeyId: null
            )
        );

    private sealed class TestSystemOrganizationManagementStore : ISystemOrganizationManagementStore
    {
        public Queue<SystemOrganizationPage<SystemOrganizationSummary>> OrganizationPages { get; } =
        [];
        public Dictionary<string, SystemOrganizationDetails> Details { get; } = [];
        public Dictionary<
            string,
            SystemOrganizationPage<SystemOrganizationMember>
        > MemberPages { get; } = [];
        public string? LastListOrganizationsQuery { get; private set; }
        public string? LastListMembersOrgId { get; private set; }
        public bool? LastUpdateActive { get; private set; }
        public OrganizationTier? LastUpdateTier { get; private set; }

        public Task<SystemOrganizationPage<SystemOrganizationSummary>> ListOrganizationsAsync(
            int page,
            int pageSize,
            string? query,
            CancellationToken ct
        )
        {
            LastListOrganizationsQuery = query;

            return Task.FromResult(OrganizationPages.Dequeue());
        }

        public Task<SystemOrganizationDetails?> FindOrganizationAsync(
            string orgId,
            CancellationToken ct
        ) => Task.FromResult(Details.GetValueOrDefault(orgId));

        public Task<SystemOrganizationPage<SystemOrganizationMember>> ListMembersAsync(
            string orgId,
            int page,
            int pageSize,
            CancellationToken ct
        )
        {
            LastListMembersOrgId = orgId;

            return Task.FromResult(MemberPages[orgId]);
        }

        public Task<SystemOrganizationDetails?> UpdateOrganizationAdminStateAsync(
            string orgId,
            bool? active,
            OrganizationTier? tier,
            CancellationToken ct
        )
        {
            LastUpdateActive = active;
            LastUpdateTier = tier;

            return Task.FromResult(Details.GetValueOrDefault(orgId));
        }
    }
}
