namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Provider-neutral file entry used by code review prompt construction and execution.
/// </summary>
/// <param name="Path">Current repository-relative file path.</param>
/// <param name="PreviousPath">Previous repository-relative file path for renames or copies.</param>
/// <param name="MutationState">How the pull request changed the file.</param>
/// <param name="Patch">Unified patch content for text files, or an empty string for binary/no-patch files.</param>
public sealed record CodeReviewFileSnapshot(
    string Path,
    string? PreviousPath,
    CodeReviewFileMutationState MutationState,
    string Patch
);
