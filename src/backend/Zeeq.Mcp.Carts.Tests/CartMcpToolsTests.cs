using System.Security.Claims;
using Zeeq.Core.Carts;
using Zeeq.Core.Identity;
using Zeeq.Mcp.Carts;

namespace Zeeq.Mcp.Carts.Tests;

/// <summary>
/// MCP tool tests that lock in org-scoped retrieval and distinguish saved
/// server carts from local-only drafts.
/// </summary>
public sealed class CartMcpToolsTests
{
    [Test]
    public async Task GetCartFindings_WithUnauthenticatedUser_ReturnsOrgRequiredMessage()
    {
        var store = new TestCartStore();
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        var response = await CartMcpTools.GetCartFindings(
            store,
            anonymous,
            "snappy-lake-a1b2c3d4e5"
        );

        await Assert.That(response).IsEqualTo("Active organization is required.");
    }

    [Test]
    public async Task GetCartFindings_WithDraftCartIdThatWasNeverSaved_ReturnsNotFoundMessage()
    {
        var store = new TestCartStore();

        var response = await CartMcpTools.GetCartFindings(
            store,
            TestUser(),
            "gentle-star-b1c2d3e4f5"
        );

        await Assert.That(response).Contains("not found");
        await Assert.That(response).Contains("gentle-star-b1c2d3e4f5");
    }

    [Test]
    public async Task GetCartFindings_WithCartFromDifferentOrganization_ReturnsNotFoundMessage()
    {
        var store = new TestCartStore { StoredCart = TestCart(organizationId: "other_org") };

        var response = await CartMcpTools.GetCartFindings(
            store,
            TestUser(orgId: "org_123"),
            "snappy-lake-a1b2c3d4e5"
        );

        await Assert.That(response).Contains("not found");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static ClaimsPrincipal TestUser(string orgId = "org_123") =>
        new(
            new ClaimsIdentity(
                [new Claim(AuthClaims.OrganizationId, orgId), new Claim("sub", "user_456")],
                authenticationType: "test"
            )
        );

    private static Cart TestCart(
        string organizationId = "org_123",
        string ownerUserId = "user_456",
        string? annotation = null
    )
    {
        var now = DateTimeOffset.UtcNow;

        return new()
        {
            Id = "snappy-lake-a1b2c3d4e5",
            OrganizationId = organizationId,
            OwnerUserId = ownerUserId,
            Name = "snappy-lake-a1b2c3d4e5",
            ItemSummaries =
            [
                new CartFindingSummary(
                    "abc123",
                    "Test finding",
                    "Security",
                    "A test finding summary",
                    "Critical",
                    annotation
                ),
            ],
            ItemsPayload =
            [
                new CartFindingSnapshot(
                    "abc123",
                    "Test finding",
                    "Critical",
                    "src/test.cs",
                    42,
                    "RIGHT",
                    "A test finding summary",
                    "Finding body text",
                    "owner/repo",
                    1,
                    "Security",
                    "Test Agent",
                    annotation,
                    now
                ),
            ],
            CreatedAtUtc = now,
            SavedAtUtc = now,
            UpdatedAtUtc = now,
        };
    }

    // ── Fake store ────────────────────────────────────────────────────────

    private sealed class TestCartStore : ICartStore
    {
        public Cart? StoredCart { get; set; }

        public Task<Cart> CreateAsync(Cart cart, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Cart>> ListForOwnerAsync(
            string organizationId,
            string ownerUserId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<Cart?> FindAsync(string organizationId, string cartId, CancellationToken ct) =>
            Task.FromResult(
                StoredCart is not null
                && StoredCart.OrganizationId == organizationId
                && StoredCart.Id == cartId
                    ? StoredCart
                    : null
            );

        public Task<bool> DeleteAsync(
            string organizationId,
            string ownerUserId,
            string cartId,
            CancellationToken ct
        ) => throw new NotSupportedException();
    }
}
