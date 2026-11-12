namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Content-addressed reference to one immutable evaluation artifact.
/// </summary>
/// <remarks>
/// Storage paths are server-generated and never accepted from clients. The byte count
/// is recorded for diagnostics and streaming limits, but integrity is proven by
/// verifying <see cref="Sha256"/> on every read.
/// </remarks>
/// <param name="StorageUri">Storage URI returned by the artifact writer.</param>
/// <param name="Sha256">Lower-case hexadecimal SHA-256 of the artifact bytes.</param>
/// <param name="ByteCount">Artifact length in bytes.</param>
public sealed record CodeReviewEvaluationArtifact(string StorageUri, string Sha256, int ByteCount);

/// <summary>
/// Immutable artifact storage for reviewer backtesting snapshots and candidate output.
/// </summary>
/// <remarks>
/// This wrapper exists instead of <see cref="ICodeReviewArtifactStore"/> because
/// evaluation artifacts are not production review records. Paths are globally unique,
/// writes are rejected once a path exists, no expiry is set, and reads verify the
/// recorded SHA-256. Callers must authorize the parent run or dataset before reading.
/// </remarks>
public interface ICodeReviewEvaluationArtifactStore
{
    /// <summary>Writes one immutable pull request snapshot artifact.</summary>
    Task<CodeReviewEvaluationArtifact> WriteSnapshotAsync(
        string organizationId,
        string repositoryId,
        CodeReviewEvaluationSnapshot snapshot,
        CancellationToken cancellationToken
    );

    /// <summary>Writes one immutable candidate reviewer output artifact.</summary>
    Task<CodeReviewEvaluationArtifact> WriteCandidateOutputAsync(
        string organizationId,
        string repositoryId,
        string runId,
        string caseId,
        string findingsXml,
        CancellationToken cancellationToken
    );

    /// <summary>Reads and verifies one snapshot artifact.</summary>
    Task<CodeReviewEvaluationSnapshot> ReadSnapshotAsync(
        string organizationId,
        string repositoryId,
        CodeReviewEvaluationArtifact artifact,
        CancellationToken cancellationToken
    );

    /// <summary>Reads and verifies one candidate output artifact.</summary>
    Task<string> ReadCandidateOutputAsync(
        string organizationId,
        string repositoryId,
        CodeReviewEvaluationArtifact artifact,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Thrown when stored artifact bytes no longer match their recorded SHA-256 or length.
/// </summary>
/// <remarks>
/// Artifacts are immutable, so a mismatch means corruption or a substituted path. Callers
/// must treat it as a hard failure, never as a cache miss.
/// </remarks>
public sealed class CodeReviewEvaluationArtifactIntegrityException : Exception
{
    /// <summary>Creates an integrity failure for one artifact.</summary>
    public CodeReviewEvaluationArtifactIntegrityException(string message)
        : base(message) { }
}
