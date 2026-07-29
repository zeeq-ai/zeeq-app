namespace Zeeq.Core.Documents;

/// <summary>
/// One <c>zeeq_placeholder</c> region declared in an organization prompt document.
/// </summary>
/// <remarks>
/// This is the editing-surface projection: it materializes every attribute so the repository
/// configuration UI can render a labelled input per placeholder. The MCP retrieval path does not
/// use this type — see <see cref="PromptPlaceholderParser.Substitute" />, which reads only the
/// <c>name</c> attribute and never allocates the display metadata.
/// </remarks>
/// <param name="Name">
/// Slug identifying the placeholder, for example <c>testing_rules</c>. This is the stable key a
/// repository's saved override is stored against, so renaming it in the document orphans any
/// previously saved value.
/// </param>
/// <param name="DisplayName">
/// Optional human-readable label shown above the input. Falls back to <paramref name="Name" /> in
/// the UI when absent.
/// </param>
/// <param name="Description">Optional helper text explaining what the repository should supply.</param>
/// <param name="DefaultValue">
/// The tag body, whitespace-trimmed. Substituted whenever a repository has no override for this
/// placeholder. An empty body yields an empty string rather than leaving the tag in place.
/// </param>
public sealed record PromptPlaceholder(
    string Name,
    string? DisplayName,
    string? Description,
    string DefaultValue
);
