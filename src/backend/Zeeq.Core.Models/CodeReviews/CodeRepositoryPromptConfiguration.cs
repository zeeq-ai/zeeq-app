namespace Zeeq.Core.Models;

/// <summary>
/// A repository's activation and placeholder overrides for one organization MCP prompt.
/// </summary>
/// <remarks>
/// Organization prompts are library documents marked as organization-scoped skills; they are shared
/// across the whole tenant and describe a workflow in general terms. A repository frequently needs
/// the same workflow with locally specific rules (language, platform, test runner), so a prompt can
/// declare <c>zeeq_placeholder</c> regions and each repository can supply its own values here.
///
/// Flow: an MCP client sends <c>x-zeeq-prompts-repo: owner/repo</c> with <c>prompts/get</c>.
/// <c>DynamicPromptsService</c> resolves that repository inside the caller's organization, loads the
/// row for the requested prompt, and — only when <see cref="Active" /> is set — applies
/// <see cref="PlaceholderValues" /> through <c>PromptPlaceholderParser.Substitute</c>. Without a
/// header, without a matching repository, or with an inactive row, every placeholder renders its
/// authored default instead.
///
/// This is deliberately <b>not</b> modeled as <c>LibraryDocumentScopedSkill.Repository</c>. That
/// reserved enum value means "this document is itself scoped to a repository", which is a different
/// (unimplemented) feature. This entity is a many-to-many relation between an existing
/// organization-scoped prompt and the repositories that customize it.
///
/// Rows are keyed on the document rather than the prompt name because prompt names are derived and
/// can change when a document is renamed or its skill metadata is edited; the document identity is
/// stable, so saved values survive those edits.
/// </remarks>
public sealed class CodeRepositoryPromptConfiguration
    : MutableDomainEntityBase,
        IOrganizationScopedEntity
{
    /// <inheritdoc />
    public required string OrganizationId { get; init; }

    /// <summary>Optional team context, inherited from the owning repository mapping.</summary>
    public string? TeamId { get; set; }

    /// <summary>Repository mapping that owns these prompt settings.</summary>
    public required string RepositoryId { get; set; }

    /// <summary>Library containing the prompt document.</summary>
    public required string LibraryId { get; set; }

    /// <summary>Stable identifier of the prompt document being customized.</summary>
    public required string DocumentId { get; set; }

    /// <summary>
    /// Whether this repository's values are applied when the prompt is retrieved.
    /// </summary>
    /// <remarks>
    /// Activation gates substitution rather than merely controlling UI presentation: deactivating a
    /// prompt for a repository must actually stop its customization from reaching an agent, while
    /// preserving the saved values so it can be switched back on. Note this never affects prompt
    /// discovery — the prompt remains listed and retrievable for every caller regardless.
    /// </remarks>
    public bool Active { get; set; }

    /// <summary>
    /// Placeholder values keyed by the <c>name</c> attribute declared in the prompt body.
    /// </summary>
    /// <remarks>
    /// Sparse by design: only placeholders the repository actually customizes are stored, and any
    /// name with no entry falls back to the authored default. Entries whose names no longer appear
    /// in the document are harmless — substitution ignores them — which keeps a prompt edit from
    /// breaking every repository that configured it.
    ///
    /// Materialized with <see cref="StringComparer.Ordinal" /> by the EF conversion so the retrieval
    /// path can probe it with a span alternate lookup and avoid allocating a string per placeholder.
    /// </remarks>
    public Dictionary<string, string> PlaceholderValues { get; set; } = new(StringComparer.Ordinal);
}
