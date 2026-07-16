using Zeeq.Core.Common.Storage;
using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Data.Postgres.CodeReviews;

/// <summary>
/// Postgres-backed artifact store for code-review findings.
/// </summary>
internal sealed class PostgresCodeReviewArtifactStore(
    IStorageProvider<PostgresStorageWriteOptions> storage
) : ICodeReviewArtifactStore
{
    /// <inheritdoc />
    public Task<string> WriteFindingsAsync(
        CodeReviewRecord review,
        Stream findings,
        string contentType,
        CancellationToken cancellationToken
    )
    {
        var path =
            $"code-review-findings/{review.OrganizationId}/{review.Id}/{review.CreatedAtUtc:yyyyMMddHHmmssfffffff}.xml";

        return storage.WriteAsync(
            path,
            findings,
            contentType,
            new PostgresStorageWriteOptions { OrganizationId = review.OrganizationId },
            cancellationToken: cancellationToken
        );
    }

    /// <inheritdoc />
    public Task<Stream> OpenFindingsAsync(
        string findingsStorageUri,
        CancellationToken cancellationToken
    ) => storage.ReadAsync(ToPath(findingsStorageUri), cancellationToken: cancellationToken);

    /// <inheritdoc />
    public Task CopyFindingsToAsync(
        string findingsStorageUri,
        Stream destination,
        CancellationToken cancellationToken
    ) =>
        storage.CopyToAsync(
            ToPath(findingsStorageUri),
            destination,
            cancellationToken: cancellationToken
        );

    private static string ToPath(string storageUri) =>
        storageUri.StartsWith("postgres://", StringComparison.Ordinal)
            ? storageUri["postgres://".Length..]
            : storageUri;
}
