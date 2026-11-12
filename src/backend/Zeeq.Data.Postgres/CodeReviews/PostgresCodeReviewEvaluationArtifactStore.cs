using System.Security.Cryptography;
using System.Text;
using Zeeq.Core.Common.Storage;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Data.Postgres.CodeReviews;

/// <summary>
/// Postgres-backed immutable artifact store for evaluation snapshots and candidate output.
/// </summary>
/// <remarks>
/// Paths are server-generated and globally distinct. A write is rejected when the path
/// already exists, so a published artifact can never be overwritten. Reads verify the
/// recorded SHA-256 and byte count, which means a corrupted or substituted object is a hard
/// failure instead of a silent recapture. No expiry is set on referenced artifacts.
/// </remarks>
internal sealed class PostgresCodeReviewEvaluationArtifactStore(
    IStorageProvider<PostgresStorageWriteOptions> storage
) : ICodeReviewEvaluationArtifactStore
{
    private const string RootPrefix = "reviewer-evaluations";

    /// <inheritdoc />
    public async Task<CodeReviewEvaluationArtifact> WriteSnapshotAsync(
        string organizationId,
        string repositoryId,
        CodeReviewEvaluationSnapshot snapshot,
        CancellationToken cancellationToken
    )
    {
        var bytes = CodeReviewEvaluationSnapshotSerializer.Serialize(snapshot);
        if (bytes.Length > CodeReviewEvaluationSnapshot.MaxSerializedBytes)
        {
            throw new InvalidOperationException(
                "The captured snapshot exceeds the 5 MiB evaluation limit."
            );
        }

        var path = BuildPath(organizationId, repositoryId, "snapshot.json");
        await WriteImmutableAsync(organizationId, path, bytes, cancellationToken);

        return new(
            ToStorageUri(path),
            Convert.ToHexStringLower(SHA256.HashData(bytes)),
            bytes.Length
        );
    }

    /// <inheritdoc />
    public async Task<CodeReviewEvaluationArtifact> WriteCandidateOutputAsync(
        string organizationId,
        string repositoryId,
        string runId,
        string caseId,
        string findingsXml,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(caseId);

        var bytes = Encoding.UTF8.GetBytes(findingsXml);
        var path = BuildPath(
            organizationId,
            repositoryId,
            $"runs/{Sanitize(runId)}/{Sanitize(caseId)}-output.xml"
        );
        await WriteImmutableAsync(organizationId, path, bytes, cancellationToken);

        return new(
            ToStorageUri(path),
            Convert.ToHexStringLower(SHA256.HashData(bytes)),
            bytes.Length
        );
    }

    /// <inheritdoc />
    public async Task<CodeReviewEvaluationSnapshot> ReadSnapshotAsync(
        string organizationId,
        string repositoryId,
        CodeReviewEvaluationArtifact artifact,
        CancellationToken cancellationToken
    )
    {
        var bytes = await ReadVerifiedAsync(
            organizationId,
            repositoryId,
            artifact,
            cancellationToken
        );

        return CodeReviewEvaluationSnapshotSerializer.Deserialize(Encoding.UTF8.GetString(bytes));
    }

    /// <inheritdoc />
    public async Task<string> ReadCandidateOutputAsync(
        string organizationId,
        string repositoryId,
        CodeReviewEvaluationArtifact artifact,
        CancellationToken cancellationToken
    )
    {
        var bytes = await ReadVerifiedAsync(
            organizationId,
            repositoryId,
            artifact,
            cancellationToken
        );

        return Encoding.UTF8.GetString(bytes);
    }

    private static string BuildPath(
        string organizationId,
        string repositoryId,
        string relativeSuffix
    ) =>
        $"{RootPrefix}/{Sanitize(organizationId)}/{Sanitize(repositoryId)}/{Guid.CreateVersion7():N}/{relativeSuffix}";

    private static string Sanitize(string value) =>
        string.Concat(
            value.Select(character =>
                char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_'
            )
        );

    private static string ToStorageUri(string path) => $"postgres://{path}";

    private static string ToPath(string storageUri) =>
        storageUri.StartsWith("postgres://", StringComparison.Ordinal)
            ? storageUri["postgres://".Length..]
            : storageUri;

    private async Task WriteImmutableAsync(
        string organizationId,
        string path,
        byte[] bytes,
        CancellationToken cancellationToken
    )
    {
        if (await storage.ExistsAsync(path, cancellationToken: cancellationToken))
        {
            throw new InvalidOperationException(
                "Evaluation artifacts are immutable and the target path already exists."
            );
        }

        await storage.WriteBytesAsync(
            path,
            bytes,
            new PostgresStorageWriteOptions { OrganizationId = organizationId },
            cancellationToken: cancellationToken
        );
    }

    private async Task<byte[]> ReadVerifiedAsync(
        string organizationId,
        string repositoryId,
        CodeReviewEvaluationArtifact artifact,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(artifact);

        var path = ToPath(artifact.StorageUri);

        // Parent-scope guard: a caller can never point the reader at another organization's or
        // repository's artifact tree, even if a URI leaked.
        var expectedSegment = $"{RootPrefix}/{Sanitize(organizationId)}/{Sanitize(repositoryId)}/";
        if (!path.StartsWith(expectedSegment, StringComparison.Ordinal))
        {
            throw new CodeReviewEvaluationArtifactIntegrityException(
                "The evaluation artifact is not scoped to the authorized organization and repository."
            );
        }

        var bytes = await storage.ReadBytesAsync(path, cancellationToken: cancellationToken);
        var actualHash = Convert.ToHexStringLower(SHA256.HashData(bytes));

        if (!string.Equals(actualHash, artifact.Sha256, StringComparison.Ordinal))
        {
            throw new CodeReviewEvaluationArtifactIntegrityException(
                "The evaluation artifact hash does not match its recorded identity."
            );
        }

        if (bytes.Length != artifact.ByteCount)
        {
            throw new CodeReviewEvaluationArtifactIntegrityException(
                "The evaluation artifact length does not match its recorded identity."
            );
        }

        return bytes;
    }
}
