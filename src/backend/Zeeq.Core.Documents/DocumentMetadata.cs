namespace Zeeq.Core.Documents;

/// <summary>
/// Optional user-authored metadata associated with a library document.
/// </summary>
/// <param name="Description">Short description used when the document is presented as a skill.</param>
/// <param name="TitleOverride">Optional title shown instead of the parsed document title.</param>
public sealed record DocumentMetadata(string? Description, string? TitleOverride);
