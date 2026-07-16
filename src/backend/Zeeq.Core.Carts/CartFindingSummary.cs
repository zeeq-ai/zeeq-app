namespace Zeeq.Core.Carts;

/// <summary>
/// Lightweight per-finding row stored in <see cref="Cart.ItemSummaries"/> and returned
/// by <c>GET /carts</c>.  Deliberately excludes the full finding body — see
/// <see cref="CartFindingSnapshot"/> for the full payload.
/// </summary>
/// <param name="Hash">Client-computed SHA-256 hex digest of the finding's content.
/// Used by the frontend to detect duplicates and drive the "In cart" badge.</param>
/// <param name="Title">Display title for the saved-cart item row.</param>
/// <param name="Facet">Reviewer facet label, such as Security or Performance.</param>
/// <param name="Summary">Short finding summary.</param>
/// <param name="Criticality">Finding criticality/severity, as its string name.</param>
/// <param name="Annotation">Optional free-text note attached by the reviewer.</param>
public sealed record CartFindingSummary(
    string Hash,
    string Title,
    string Facet,
    string Summary,
    string Criticality,
    string? Annotation
);
