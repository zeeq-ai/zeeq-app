namespace Zeeq.Core.Carts;

/// <summary>
/// Full finding payload stored in <see cref="Cart.ItemsPayload"/> — only read by the
/// MCP tool, text compilation, and explicit copy-to-new-draft.  Never returned by the
/// startup <c>GET /carts</c> endpoint; that endpoint uses <see cref="CartFindingSummary"/>.
/// </summary>
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
/// <see cref="CartLimits.MaxAnnotationLength"/> chars), surfaced to the local agent as a
/// <c>note</c> attribute in the compiled XML.
/// </param>
/// <param name="AddedAtUtc">Timestamp when this finding was added to the (then-draft) cart.</param>
public sealed record CartFindingSnapshot(
    string Hash,
    string Title,
    string Criticality,
    string File,
    int? Line,
    string? Side,
    string Summary,
    string Body,
    string OwnerQualifiedRepoName,
    int PullRequestNumber,
    string Facet,
    string Agent,
    string? Annotation,
    DateTimeOffset AddedAtUtc
);
