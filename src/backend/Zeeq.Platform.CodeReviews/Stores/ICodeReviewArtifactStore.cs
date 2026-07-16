using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Code-review artifact storage wrapper for runner and renderer paths.
/// </summary>
public interface ICodeReviewArtifactStore
{
    /// <summary>Writes canonical findings XML for a completed review.</summary>
    Task<string> WriteFindingsAsync(
        CodeReviewRecord review,
        Stream findings,
        string contentType,
        CancellationToken cancellationToken
    );

    /// <summary>Opens canonical findings XML by storage URI.</summary>
    Task<Stream> OpenFindingsAsync(string findingsStorageUri, CancellationToken cancellationToken);

    /// <summary>Copies canonical findings XML to a caller-owned stream.</summary>
    Task CopyFindingsToAsync(
        string findingsStorageUri,
        Stream destination,
        CancellationToken cancellationToken
    );
}
