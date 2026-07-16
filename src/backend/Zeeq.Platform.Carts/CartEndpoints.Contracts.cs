using System.ComponentModel.DataAnnotations;
using Zeeq.Core.Carts;

namespace Zeeq.Platform.Carts;

/// <summary>
/// Error response for cart API validation failures.
/// </summary>
/// <param name="Code">Stable machine-readable error code.</param>
/// <param name="Message">Human-readable description.</param>
public sealed record CartError(string Code, string Message);

/// <param name="Id">Client-generated cart id, preserved from the draft.</param>
/// <param name="Name">Generated name, such as snappy-lake-a1b2c3d4e5.</param>
/// <param name="ItemCount">Number of findings in the cart.</param>
/// <param name="CreatedAtUtc">When the draft was originally created client-side.</param>
/// <param name="SavedAtUtc">When the initial save request was processed.</param>
/// <param name="UpdatedAtUtc">When the cart was last changed server-side.</param>
/// <param name="Items">Lightweight per-finding rows for startup/list UI.</param>
public sealed record CartResponse(
    string Id,
    string Name,
    int ItemCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset SavedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<CartFindingResponse> Items
);

/// <summary>List of the caller's saved carts with metadata-only items.</summary>
/// <param name="Items">The caller's saved carts, each with summary-only findings.</param>
public sealed record CartListResponse(IReadOnlyList<CartResponse> Items);

/// <summary>
/// Lightweight per-finding row.  Only these fields are returned by <c>GET /carts</c> —
/// the full finding body is never exposed through the startup list endpoint.
/// </summary>
/// <param name="Hash">Client-computed SHA-256 hex digest of the finding's content.</param>
/// <param name="Title">Display title for saved-cart metadata.</param>
/// <param name="Facet">Reviewer facet label, such as Security or Performance.</param>
/// <param name="Summary">Short finding summary.</param>
/// <param name="Criticality">Finding criticality/severity, as its string name.</param>
/// <param name="Annotation">Optional free-text note the user attached when adding this finding.</param>
public sealed record CartFindingResponse(
    string Hash,
    string Title,
    string Facet,
    string Summary,
    string Criticality,
    string? Annotation
);

/// <summary>One full finding snapshot inside an initial save or copy-source payload.</summary>
/// <param name="Hash">Client-computed SHA-256 hex digest of the finding's content.</param>
/// <param name="Title">Display title for saved-cart metadata and copy-source reconstruction.</param>
/// <param name="Criticality">Finding criticality/severity, as its string name.</param>
/// <param name="File">Repository-relative file path.</param>
/// <param name="Line">Optional file line for inline findings.</param>
/// <param name="Side">Optional GitHub diff side, such as RIGHT or LEFT.</param>
/// <param name="Summary">Short finding summary.</param>
/// <param name="Body">Full finding explanation (markdown).</param>
/// <param name="OwnerQualifiedRepoName">Source repository, such as owner/repo.</param>
/// <param name="PullRequestNumber">Source pull request number.</param>
/// <param name="Facet">Reviewer facet label, such as Security or Performance.</param>
/// <param name="Agent">Reviewer display name.</param>
/// <param name="Annotation">
/// Optional free-text note the user attached when adding this finding (max
/// <see cref="CartLimits.MaxAnnotationLength"/> chars).
/// </param>
/// <param name="AddedAtUtc">Timestamp when this finding was added to the (then-draft) cart.</param>
public sealed record SaveCartItemRequest(
    [property: Required, MaxLength(100)] string Hash,
    [property: Required, MaxLength(200)] string Title,
    [property: Required, MaxLength(20)] string Criticality,
    [property: Required, MaxLength(300)] string File,
    [property: Range(1, int.MaxValue)] int? Line,
    [property: MaxLength(16)] string? Side,
    [property: Required, MaxLength(500)] string Summary,
    [property: Required, MaxLength(20_000)] string Body,
    [property: Required, MaxLength(200)] string OwnerQualifiedRepoName,
    [property: Range(1, int.MaxValue)] int PullRequestNumber,
    [property: Required, MaxLength(100)] string Facet,
    [property: Required, MaxLength(100)] string Agent,
    [property: MaxLength(CartLimits.MaxAnnotationLength)] string? Annotation,
    DateTimeOffset AddedAtUtc
);

/// <summary>
/// The full client-side draft, submitted once on first save. <see cref="Id"/> and
/// <see cref="Name"/> were minted client-side when the draft was created and are
/// preserved unchanged; <see cref="CreatedAtUtc"/> is the draft's original creation
/// time, not "now".
/// </summary>
/// <param name="Id">Client-generated cart id, preserved from the draft.</param>
/// <param name="Name">Generated name, such as snappy-lake-a1b2c3d4e5.</param>
/// <param name="CreatedAtUtc">When the draft was originally created client-side.</param>
/// <param name="Items">Full finding payloads to persist for this cart.</param>
public sealed record SaveCartRequest(
    [property: Required, MaxLength(64), RegularExpression(CartLimits.CartIdPattern)] string Id,
    [property: Required, MaxLength(64)] string Name,
    DateTimeOffset CreatedAtUtc,
    [property: MaxLength(CartLimits.MaxItemsPerCart)] IReadOnlyList<SaveCartItemRequest> Items
);

/// <summary>Full saved cart contents returned only for explicit client-side copy-to-new-draft.</summary>
/// <param name="Id">Client-generated cart id, preserved from the draft.</param>
/// <param name="Name">Generated name, such as snappy-lake-a1b2c3d4e5.</param>
/// <param name="Items">Full finding payloads to seed the new draft.</param>
public sealed record CartCopySourceResponse(
    string Id,
    string Name,
    IReadOnlyList<SaveCartItemRequest> Items
);

/// <param name="Text">The compiled agent-instructions text, ready to paste.</param>
public sealed record CartTextResponse(string Text);
