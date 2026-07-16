namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Provider-neutral mutation state for a file included in a code-review source snapshot.
/// </summary>
public enum CodeReviewFileMutationState
{
    /// <summary>
    /// A file introduced by the pull request.
    /// </summary>
    Added,

    /// <summary>
    /// An existing file changed by the pull request.
    /// </summary>
    Modified,

    /// <summary>
    /// A file removed by the pull request.
    /// </summary>
    Deleted,

    /// <summary>
    /// A file moved to a new path by the pull request.
    /// </summary>
    Renamed,

    /// <summary>
    /// A file copied from an existing path by the pull request.
    /// </summary>
    Copied,

    /// <summary>
    /// A binary file whose contents are not represented as a text patch.
    /// </summary>
    Binary,
}
