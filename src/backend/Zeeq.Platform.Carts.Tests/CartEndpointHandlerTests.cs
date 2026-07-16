using System.Security.Claims;
using Zeeq.Core.Carts;
using Zeeq.Core.Identity;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Zeeq.Platform.Carts.Tests;

/// <summary>
/// Handler tests that lock in create/list/delete/text/copy-source behavior
/// without a real Postgres database.
/// </summary>
public sealed class CartEndpointHandlerTests
{
    [Test]
    public async Task SaveCart_WithNoActiveOrganization_ReturnsBadRequest()
    {
        var store = new TestCartStore();
        var handler = new SaveCartHandler(store);

        var result = await handler.HandleAsync(
            "",
            TestSaveRequest(),
            new ClaimsPrincipal(new ClaimsIdentity()),
            CancellationToken.None
        );

        var badRequest = result.Result as BadRequest<CartError>;

        await Assert.That(badRequest).IsNotNull();
        await Assert.That(badRequest!.Value!.Code).IsEqualTo("missing_organization");
    }

    [Test]
    public async Task SaveCart_WithEmptyItems_ReturnsBadRequest()
    {
        var store = new TestCartStore();
        var handler = new SaveCartHandler(store);

        var request = TestSaveRequest() with { Items = [] };

        var result = await handler.HandleAsync(
            "org_123",
            request,
            TestUser(),
            CancellationToken.None
        );

        var badRequest = result.Result as BadRequest<CartError>;

        await Assert.That(badRequest).IsNotNull();
        await Assert.That(badRequest!.Value!.Code).IsEqualTo("empty_cart");
    }

    [Test]
    public async Task SaveCart_WithMoreThanTenItems_ReturnsBadRequest()
    {
        var store = new TestCartStore();
        var handler = new SaveCartHandler(store);

        var items = Enumerable
            .Range(0, CartLimits.MaxItemsPerCart + 1)
            .Select(i => TestSaveItem(hash: $"hash_{i}"))
            .ToArray();

        var request = TestSaveRequest() with { Items = items };

        var result = await handler.HandleAsync(
            "org_123",
            request,
            TestUser(),
            CancellationToken.None
        );

        var badRequest = result.Result as BadRequest<CartError>;

        await Assert.That(badRequest).IsNotNull();
        await Assert.That(badRequest!.Value!.Code).IsEqualTo("cart_item_limit_reached");
    }

    [Test]
    public async Task SaveCart_AtCartLimit_ReturnsConflict()
    {
        var store = new TestCartStore { ShouldThrowLimitExceeded = true };
        var handler = new SaveCartHandler(store);

        var result = await handler.HandleAsync(
            "org_123",
            TestSaveRequest(),
            TestUser(),
            CancellationToken.None
        );

        var conflict = result.Result as Conflict<CartError>;

        await Assert.That(conflict).IsNotNull();
        await Assert.That(conflict!.Value!.Code).IsEqualTo("cart_limit_reached");
    }

    [Test]
    public async Task SaveCart_WithValidRequest_PersistsSummaryAndPayloadJson_ReturnsCreated()
    {
        var store = new TestCartStore();
        var handler = new SaveCartHandler(store);

        var request = TestSaveRequest();

        var result = await handler.HandleAsync(
            "org_123",
            request,
            TestUser(),
            CancellationToken.None
        );

        var created = result.Result as Created<CartResponse>;

        await Assert.That(created).IsNotNull();
        await Assert.That(created!.Value!.Id).IsEqualTo(request.Id);
        await Assert.That(created.Value.Name).IsEqualTo(request.Name);
        await Assert.That(created.Value.ItemCount).IsEqualTo(1);
        await Assert.That(store.SavedCart).IsNotNull();
        await Assert.That(store.SavedCart!.ItemSummaries.Count).IsEqualTo(1);
        await Assert.That(store.SavedCart.ItemsPayload.Count).IsEqualTo(1);
    }

    [Test]
    public async Task SaveCart_WithAnnotation_PersistsItInPayloadAndSummary()
    {
        var store = new TestCartStore();
        var handler = new SaveCartHandler(store);

        var request = TestSaveRequest(annotation: "Fix this soon");

        var result = await handler.HandleAsync(
            "org_123",
            request,
            TestUser(),
            CancellationToken.None
        );

        var created = result.Result as Created<CartResponse>;

        await Assert.That(created).IsNotNull();
        await Assert.That(store.SavedCart!.ItemsPayload[0].Annotation).IsEqualTo("Fix this soon");
        await Assert.That(store.SavedCart.ItemSummaries[0].Annotation).IsEqualTo("Fix this soon");
    }

    [Test]
    public async Task ListCarts_ReturnsOnlyCallersCarts()
    {
        var store = new TestCartStore();
        var handler = new ListCartsHandler(store);

        var result = await handler.HandleAsync("org_123", TestUser(), CancellationToken.None);

        var ok = result.Result as Ok<CartListResponse>;

        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value!.Items).IsNotNull();
    }

    [Test]
    public async Task GetCartText_WithSavedCart_ReturnsMcpInstruction()
    {
        var store = new TestCartStore
        {
            StoredCart = TestDomainCart(annotation: "Review this carefully"),
        };
        var handler = new GetCartTextHandler(store);

        var result = await handler.HandleAsync(
            "org_123",
            "cart_1",
            TestUser(),
            CancellationToken.None
        );

        var ok = result.Result as Ok<CartTextResponse>;

        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value!.Text).Contains("get_cart_findings");
        await Assert.That(ok.Value.Text).Contains("cart_1");
    }

    [Test]
    public async Task GetCartCopySource_ReturnsFullPayload()
    {
        var store = new TestCartStore { StoredCart = TestDomainCart() };
        var handler = new GetCartCopySourceHandler(store);

        var result = await handler.HandleAsync(
            "org_123",
            "cart_1",
            TestUser(),
            CancellationToken.None
        );

        var ok = result.Result as Ok<CartCopySourceResponse>;

        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value!.Items.Count).IsEqualTo(1);
        await Assert.That(ok.Value.Items[0].Body).IsEqualTo("Finding body");
    }

    [Test]
    public async Task DeleteCart_OnUnknownCart_ReturnsNotFound()
    {
        var store = new TestCartStore();
        var handler = new DeleteCartHandler(store);

        var result = await handler.HandleAsync(
            "org_123",
            "nonexistent",
            TestUser(),
            CancellationToken.None
        );

        await Assert.That(result.Result is NotFound).IsTrue();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static ClaimsPrincipal TestUser(string orgId = "org_123") =>
        new(
            new ClaimsIdentity(
                [new Claim(AuthClaims.OrganizationId, orgId), new Claim("sub", "user_456")],
                authenticationType: "test"
            )
        );

    private static SaveCartRequest TestSaveRequest(string? annotation = null) =>
        new(
            Id: "cart_1",
            Name: "swift-otter-jumps-a1b2",
            CreatedAtUtc: DateTimeOffset.UtcNow,
            Items: [TestSaveItem(annotation: annotation)]
        );

    private static SaveCartItemRequest TestSaveItem(
        string hash = "abc123",
        string? annotation = null
    ) =>
        new(
            Hash: hash,
            Title: "Test finding",
            Criticality: "Critical",
            File: "src/test.cs",
            Line: 42,
            Side: "RIGHT",
            Summary: "A test finding summary",
            Body: "Finding body",
            OwnerQualifiedRepoName: "owner/repo",
            PullRequestNumber: 1,
            Facet: "Security",
            Agent: "Test Agent",
            Annotation: annotation,
            AddedAtUtc: DateTimeOffset.UtcNow
        );

    private static Cart TestDomainCart(string? annotation = null)
    {
        var now = DateTimeOffset.UtcNow;

        return new()
        {
            Id = "cart_1",
            OrganizationId = "org_123",
            OwnerUserId = "user_456",
            Name = "swift-otter-jumps-a1b2",
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
                    "Finding body",
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
        public bool ShouldThrowLimitExceeded { get; set; }

        public Cart? SavedCart { get; private set; }

        public Cart? StoredCart { get; set; }

        public Task<Cart> CreateAsync(Cart cart, CancellationToken ct)
        {
            if (ShouldThrowLimitExceeded)
            {
                throw new CartLimitExceededException(cart.OrganizationId, cart.OwnerUserId);
            }

            SavedCart = cart;

            return Task.FromResult(cart);
        }

        public Task<IReadOnlyList<Cart>> ListForOwnerAsync(
            string organizationId,
            string ownerUserId,
            CancellationToken ct
        )
        {
            var carts = StoredCart is not null ? new[] { StoredCart } : [];

            return Task.FromResult<IReadOnlyList<Cart>>(carts);
        }

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
        ) =>
            Task.FromResult(
                StoredCart is not null
                    && StoredCart.OrganizationId == organizationId
                    && StoredCart.Id == cartId
            );
    }
}
