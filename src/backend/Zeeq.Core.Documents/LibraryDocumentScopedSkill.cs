namespace Zeeq.Core.Documents;

/// <summary>
/// Scope at which a library document is available as a skill.
/// </summary>
public enum LibraryDocumentScopedSkill
{
    /// <summary>The document is not available as a scoped skill.</summary>
    None = 0,

    /// <summary>The document is available as a skill to the organization.</summary>
    Organization = 1,

    /// <summary>The document is available as a skill within its library.</summary>
    Library = 2,

    /// <summary>The document is available as a skill within a repository scope.</summary>
    Repository = 3,

    /// <summary>The document is available as a skill to one user.</summary>
    User = 4,
}
