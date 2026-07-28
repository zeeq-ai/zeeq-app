using System.ComponentModel.DataAnnotations;

namespace Zeeq.Integrations.GitHub;

/// <summary>
/// One organization prompt as seen from a repository's configuration surface.
/// </summary>
/// <remarks>
/// Deliberately excludes the prompt body and its declared placeholders. The configuration UI renders
/// these as a collapsed accordion, so the list only needs enough to label each row and show whether
/// the repository has customized it; placeholders are loaded when a row is expanded. That keeps the
/// list a single query instead of one content read per prompt.
/// </remarks>
/// <param name="DocumentId">
/// Stable identifier of the prompt document. Combined with <paramref name="LibraryId" /> this is the
/// key used to save configuration, and it is stable across prompt renames.
/// </param>
/// <param name="LibraryId">Library that owns the prompt document.</param>
/// <param name="LibraryName">Human-readable library name, for grouping prompts in the UI.</param>
/// <param name="Path">Document path within the library, for example <c>/prompts/review-flow.md</c>.</param>
/// <param name="Title">Document title shown as the accordion label.</param>
/// <param name="Description">
/// Prompt description using the same precedence agents see: manual skill description, then parsed
/// front matter, then the document title.
/// </param>
/// <param name="Active">
/// Whether this repository's saved values are applied when an agent retrieves the prompt. Inactive
/// prompts still render their authored defaults.
/// </param>
/// <param name="ConfiguredValueCount">
/// How many placeholder values this repository has saved, so the UI can badge a row as customized
/// without loading the prompt body.
/// </param>
public sealed record RepositoryPromptSummaryResponse(
    string DocumentId,
    string LibraryId,
    string LibraryName,
    string Path,
    string Title,
    string Description,
    bool Active,
    int ConfiguredValueCount
);

/// <summary>
/// One placeholder declared by a prompt, paired with the repository's saved value.
/// </summary>
/// <param name="Name">
/// Stable key a saved value is stored against. This comes from the prompt's explicit <c>name</c>
/// attribute when present; otherwise it is derived from <paramref name="Label" />.
/// </param>
/// <param name="Label">
/// Optional human-readable label. The UI falls back to <paramref name="Name" /> when absent.
/// </param>
/// <param name="Description">Optional helper text explaining what the repository should supply.</param>
/// <param name="DefaultValue">
/// Authored fallback used whenever <paramref name="Value" /> is absent. Shown as placeholder text so
/// the user can see what they are overriding.
/// </param>
/// <param name="Value">
/// This repository's override, or <see langword="null" /> when it has not customized this
/// placeholder. Null and empty string are meaningfully different: null falls back to the authored
/// default, while empty string is an explicit "render nothing here".
/// </param>
public sealed record RepositoryPromptPlaceholderResponse(
    string Name,
    string? Label,
    string? Description,
    string DefaultValue,
    string? Value
);

/// <summary>
/// A single prompt with every placeholder it declares and this repository's saved values.
/// </summary>
/// <remarks>
/// Placeholders are parsed from the live prompt body on each request rather than stored, so an edit
/// to the prompt document immediately shows up as added or removed inputs. Values saved against a
/// placeholder that no longer exists are simply not returned here; they stay in storage and are
/// ignored at substitution time, so renaming a placeholder never destroys data.
/// </remarks>
/// <param name="DocumentId">Stable identifier of the prompt document.</param>
/// <param name="LibraryId">Library that owns the prompt document.</param>
/// <param name="LibraryName">Human-readable library name.</param>
/// <param name="Path">Document path within the library.</param>
/// <param name="Title">Document title.</param>
/// <param name="Description">Prompt description, using the same precedence agents see.</param>
/// <param name="Active">
/// Whether this repository's saved values are applied when an agent retrieves the prompt.
/// </param>
/// <param name="Placeholders">
/// Every placeholder the prompt currently declares, in document order, each carrying this
/// repository's saved value when one exists.
/// </param>
public sealed record RepositoryPromptDetailResponse(
    string DocumentId,
    string LibraryId,
    string LibraryName,
    string Path,
    string Title,
    string Description,
    bool Active,
    RepositoryPromptPlaceholderResponse[] Placeholders
);

/// <summary>
/// Request to activate a prompt for a repository and set its placeholder values.
/// </summary>
/// <remarks>
/// Activation gates substitution rather than only UI presentation: deactivating stops this
/// repository's values from reaching an agent while preserving them for later reactivation.
/// </remarks>
/// <param name="LibraryId">
/// Library owning the prompt document. Part of the prompt's identity, so it is required even though
/// the document id appears in the route.
/// </param>
/// <param name="Active">
/// <see langword="true" /> to apply this repository's values when an agent retrieves the prompt;
/// <see langword="false" /> to fall back to authored defaults while retaining the saved values.
/// </param>
/// <param name="Values">
/// Placeholder values keyed by declared name. Replaced wholesale, so send the complete set to
/// persist; omitting a name falls back to that placeholder's authored default. A value may be at
/// most 8,000 characters, and at most 100 values may be stored for one prompt.
/// </param>
public sealed record SaveRepositoryPromptRequest(
    [property: Required, MaxLength(128), RegularExpression(@".*\S.*")] string LibraryId,
    bool Active,
    Dictionary<string, string>? Values = null
);

/// <summary>
/// Request to replace only the library mapping for a repository.
/// </summary>
/// <remarks>
/// Intentionally separate from <see cref="GitHubUpdateRepositoryMappingRequest" />, which also
/// carries <c>Enabled</c>, <c>DisplayName</c>, and <c>TeamId</c> and is therefore restricted to
/// owners and admins. Library mapping is ordinary configuration that any organization member should
/// be able to adjust, so it needs a request shape that cannot express a privileged change.
/// </remarks>
/// <param name="LibraryIds">
/// Complete set of library ids reviewer agents may query for this repository. Replaced wholesale;
/// send an empty array to unmap every library.
/// </param>
public sealed record UpdateRepositoryLibrariesRequest(string[] LibraryIds);
