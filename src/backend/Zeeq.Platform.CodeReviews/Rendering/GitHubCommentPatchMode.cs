namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Describes how a renderer patch changes a DOM section.
/// </summary>
public enum GitHubCommentPatchMode
{
    /// <summary>Replace the section body, creating the section when missing.</summary>
    ReplaceSection = 1,

    /// <summary>Create the section only when it does not already exist.</summary>
    InsertIfMissing = 2,

    /// <summary>Remove the section from the next rendered comment body.</summary>
    RemoveSection = 3,
}
