namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Maps uploaded-diff parser output into the provider-neutral code-review snapshot model.
/// </summary>
public static class UploadedDiffFileMapping
{
    extension(UploadedDiffFile file)
    {
        /// <summary>
        /// Converts an uploaded-diff file entry into a code-review file snapshot.
        /// </summary>
        public CodeReviewFileSnapshot ToCodeReviewFileSnapshot() =>
            new(
                file.Path,
                file.PreviousPath,
                (CodeReviewFileMutationState)file.MutationState,
                file.Patch
            );
    }

    extension(IReadOnlyList<UploadedDiffFile> files)
    {
        /// <summary>
        /// Converts uploaded-diff file entries into code-review file snapshots.
        /// </summary>
        public IReadOnlyList<CodeReviewFileSnapshot> ToCodeReviewFileSnapshots() =>
            [.. files.Select(file => file.ToCodeReviewFileSnapshot())];
    }
}
