using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Captures, validates and stages one pull request snapshot for an evaluation dataset.
/// </summary>
/// <remarks>
/// Capture performs provider I/O and artifact writes before any dataset transaction starts.
/// The result is carried to Save as a signed token, so Save never calls a provider or storage
/// and never accepts a client-supplied artifact URI.
/// </remarks>
public sealed class CodeReviewEvaluationCaptureService(
    ICodeReviewEvaluationPullRequestCaptureSource source,
    ICodeReviewEvaluationArtifactStore artifacts,
    CodeReviewEvaluationCaptureTokenProtector tokens
)
{
    /// <summary>Captures and stages one pull request for one dataset version.</summary>
    public async Task<CodeReviewEvaluationCaptureOutcome> CaptureAsync(
        string organizationId,
        string repositoryId,
        string ownerQualifiedRepoName,
        string datasetId,
        long expectedDatasetVersion,
        int pullRequestNumber,
        CancellationToken cancellationToken
    )
    {
        var snapshot = await source.CaptureAsync(
            new(organizationId, repositoryId, ownerQualifiedRepoName, pullRequestNumber),
            cancellationToken
        );

        ValidateSnapshot(snapshot, organizationId, repositoryId, pullRequestNumber);

        var artifactBytes = CodeReviewEvaluationSnapshotSerializer.Serialize(snapshot).Length;
        if (artifactBytes > CodeReviewEvaluationLimits.MaxSnapshotBytes)
        {
            throw new CodeReviewEvaluationCaptureException(
                "snapshot_too_large",
                "The captured pull request exceeds the 5 MiB evaluation snapshot limit."
            );
        }

        var artifact = await artifacts.WriteSnapshotAsync(
            organizationId,
            repositoryId,
            snapshot,
            cancellationToken
        );

        var capture = new CodeReviewEvaluationStagedCapture(
            artifact,
            snapshot.PullRequestNumber,
            snapshot.Title,
            snapshot.AuthorLogin,
            snapshot.BaseSha,
            snapshot.HeadSha,
            snapshot.State,
            snapshot.CapturedAtUtc,
            snapshot.ChangedFileCount,
            snapshot.OmittedFileCount,
            snapshot.DeclaredChangedFileCount,
            PullRequestRecordId: null
        );

        var payload = tokens.CreatePayload(
            organizationId,
            repositoryId,
            datasetId,
            expectedDatasetVersion,
            capture
        );

        return new(capture, snapshot.Title, tokens.Protect(payload), payload.ExpiresAtUtc);
    }

    /// <summary>Validates the invariants that make a snapshot trustworthy.</summary>
    private static void ValidateSnapshot(
        CodeReviewEvaluationSnapshot snapshot,
        string organizationId,
        string repositoryId,
        int pullRequestNumber
    )
    {
        // Identity first: a provider or fixture adapter must never install one repository's or
        // pull request's bytes under another parent.
        if (
            !string.Equals(snapshot.OrganizationId, organizationId, StringComparison.Ordinal)
            || !string.Equals(snapshot.RepositoryId, repositoryId, StringComparison.Ordinal)
            || snapshot.PullRequestNumber != pullRequestNumber
        )
        {
            throw new CodeReviewEvaluationCaptureException(
                "snapshot_identity_mismatch",
                "The captured snapshot does not match the requested organization, repository and pull request."
            );
        }

        if (
            string.IsNullOrWhiteSpace(snapshot.HeadSha)
            || string.IsNullOrWhiteSpace(snapshot.BaseSha)
        )
        {
            throw new CodeReviewEvaluationCaptureException(
                "snapshot_incomplete",
                "The provider did not report base and head identities for this pull request."
            );
        }

        if (snapshot.DeclaredChangedFileCount > 0 && snapshot.Files.Count == 0)
        {
            throw new CodeReviewEvaluationCaptureException(
                "snapshot_incomplete",
                "The provider reported changed files but none were fetched."
            );
        }

        // A provider can silently truncate the file list. Comparing declared and fetched
        // counts is the only cheap signal that the snapshot would understate coverage.
        if (
            snapshot.DeclaredChangedFileCount > 0
            && snapshot.Files.Count != snapshot.DeclaredChangedFileCount
        )
        {
            throw new CodeReviewEvaluationCaptureException(
                "snapshot_incomplete",
                "The captured file list does not match the provider's declared changed-file count."
            );
        }

        foreach (var file in snapshot.Files)
        {
            if (string.IsNullOrWhiteSpace(file.Path))
            {
                throw new CodeReviewEvaluationCaptureException(
                    "snapshot_incomplete",
                    "A captured file entry has no path."
                );
            }
        }
    }
}
