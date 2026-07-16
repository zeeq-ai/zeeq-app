using Zeeq.Core.Carts;

namespace Zeeq.Platform.Carts;

/// <summary>
/// Maps between domain <see cref="Cart"/> entities and API DTOs.  All mapping logic
/// lives here so handlers stay focused on validation and orchestration.
/// </summary>
/// <remarks>
/// <b>Dual-storage mapping.</b>  <see cref="ToCart"/> builds both
/// <see cref="Cart.ItemSummaries"/> (from <see cref="CartFindingSnapshot"/> →
/// <see cref="CartFindingSummary"/> projection) and <see cref="Cart.ItemsPayload"/>
/// (direct conversion) from a single <see cref="SaveCartRequest"/>.  This ensures both
/// JSON columns are populated in one pass, and the summary always matches the payload.
/// </remarks>
internal static class CartEndpointMapping
{
    extension(Cart cart)
    {
        /// <summary>
        /// Projects a persisted <see cref="Cart"/> into the lightweight <see cref="CartResponse"/>
        /// returned by <c>GET /carts</c> (wrapped in <see cref="CartListResponse"/>) and by
        /// <c>POST /carts</c> on save. Carries only <see cref="Cart.ItemSummaries"/> rows — never
        /// the full <see cref="Cart.ItemsPayload"/> — since this is safe to send on every
        /// application startup and for every saved cart at once.
        /// </summary>
        public CartResponse ToResponse() =>
            new(
                cart.Id,
                cart.Name,
                cart.ItemSummaries.Count,
                cart.CreatedAtUtc,
                cart.SavedAtUtc,
                cart.UpdatedAtUtc,
                [.. cart.ItemSummaries.Select(item => item.ToResponse())]
            );
    }

    extension(CartFindingSummary item)
    {
        /// <summary>
        /// Converts a stored finding summary into its wire-level <see cref="CartFindingResponse"/>
        /// row for the <see cref="CartResponse"/> item list.
        /// </summary>
        public CartFindingResponse ToResponse() =>
            new(item.Hash, item.Title, item.Facet, item.Summary, item.Criticality, item.Annotation);
    }

    extension(SaveCartItemRequest request)
    {
        /// <summary>
        /// Converts one submitted finding into the durable <see cref="CartFindingSnapshot"/> stored
        /// in <see cref="Cart.ItemsPayload"/>, normalizing the annotation along the way.
        /// </summary>
        public CartFindingSnapshot ToSnapshot() =>
            new(
                request.Hash,
                request.Title,
                request.Criticality,
                request.File,
                request.Line,
                request.Side,
                request.Summary,
                request.Body,
                request.OwnerQualifiedRepoName,
                request.PullRequestNumber,
                request.Facet,
                request.Agent,
                NormalizeAnnotation(request.Annotation),
                request.AddedAtUtc
            );
    }

    extension(SaveCartRequest request)
    {
        /// <summary>
        /// Builds the <see cref="Cart"/> to persist from a first-save request. Populates both
        /// <see cref="Cart.ItemSummaries"/> and <see cref="Cart.ItemsPayload"/> from a single pass
        /// over <see cref="SaveCartRequest.Items"/> so the two JSON columns can never drift apart.
        /// </summary>
        public Cart ToCart(string organizationId, string? teamId, string ownerUserId)
        {
            var snapshots = request.Items.Select(item => item.ToSnapshot()).ToArray();
            var now = DateTimeOffset.UtcNow;

            return new()
            {
                Id = request.Id,
                OrganizationId = organizationId,
                TeamId = teamId,
                OwnerUserId = ownerUserId,
                Name = request.Name,
                ItemSummaries = [.. snapshots.Select(snapshot => snapshot.ToSummary())],
                ItemsPayload = snapshots,
                CreatedAtUtc = request.CreatedAtUtc,
                SavedAtUtc = now,
                UpdatedAtUtc = now,
            };
        }
    }

    extension(CartFindingSnapshot item)
    {
        /// <summary>
        /// Projects a full finding snapshot down to the lightweight <see cref="CartFindingSummary"/>
        /// stored in <see cref="Cart.ItemSummaries"/> for cheap startup listing.
        /// </summary>
        private CartFindingSummary ToSummary() =>
            new(item.Hash, item.Title, item.Facet, item.Summary, item.Criticality, item.Annotation);
    }

    /// <summary>
    /// Trims and caps a submitted annotation to <see cref="CartLimits.MaxAnnotationLength"/>,
    /// treating whitespace-only as absent.
    /// </summary>
    private static string? NormalizeAnnotation(string? annotation)
    {
        var trimmed = annotation?.Trim();

        return string.IsNullOrEmpty(trimmed)
            ? null
            : trimmed[..Math.Min(trimmed.Length, CartLimits.MaxAnnotationLength)];
    }
}
