using System.Security.Cryptography;
using System.Text.Json;
using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Evaluation-owned capture envelope for one pull request snapshot.
/// </summary>
/// <remarks>
/// This wraps the shared production <see cref="CodeReviewPullRequestSnapshot"/> instead of
/// expanding it. The production prompt path keeps its existing record, while evaluation
/// gains base/head identities, capture time, provider-declared file counts and omission
/// markers. Developer feedback comments and previous generated reviews are stripped before
/// hashing and never appear in the envelope.
///
/// Storage boundary: this envelope, serialized into
/// <c>reviewer-evaluations/{org}/{repo}/{uniqueArtifactId}/snapshot.json</c>, is the single
/// stored capture. <see cref="ToPullRequestSnapshot"/> is a pure in-memory projection used to
/// build the execution input and is never persisted a second time. The run manifest carries
/// capture metadata plus the artifact reference (URI, SHA-256, byte count), never file
/// content. Readers verify hash and length before projecting, and a mismatch is a hard
/// failure rather than a reason to recapture. Reference findings are never part of this
/// envelope or the projection; they reach the judge only, from the manifest case.
/// </remarks>
/// <param name="SchemaVersion">Envelope schema version. Bump when the JSON shape changes.</param>
/// <param name="OrganizationId">Organization that owns the capture.</param>
/// <param name="RepositoryId">Repository mapping the capture belongs to.</param>
/// <param name="OwnerQualifiedRepoName">Provider-qualified repository name.</param>
/// <param name="PullRequestNumber">Provider pull request number.</param>
/// <param name="Title">Pull request title at capture time.</param>
/// <param name="Body">Pull request body at capture time.</param>
/// <param name="BaseSha">Base commit identity at capture time.</param>
/// <param name="HeadSha">Head commit identity at capture time.</param>
/// <param name="AuthorLogin">Provider login of the pull request author.</param>
/// <param name="HtmlUrl">Browser URL for the pull request.</param>
/// <param name="IsDraft">Whether the pull request was a draft at capture time.</param>
/// <param name="State">Provider lifecycle state at capture time.</param>
/// <param name="CapturedAtUtc">UTC time the snapshot was captured.</param>
/// <param name="DeclaredChangedFileCount">Changed-file count declared by the provider.</param>
/// <param name="Files">Changed files included in the snapshot, including explicitly omitted entries.</param>
public sealed record CodeReviewEvaluationSnapshot(
    int SchemaVersion,
    string OrganizationId,
    string RepositoryId,
    string OwnerQualifiedRepoName,
    int PullRequestNumber,
    string Title,
    string Body,
    string BaseSha,
    string HeadSha,
    string AuthorLogin,
    string HtmlUrl,
    bool IsDraft,
    PullRequestState State,
    DateTimeOffset CapturedAtUtc,
    int DeclaredChangedFileCount,
    IReadOnlyList<CodeReviewEvaluationSnapshotFile> Files
)
{
    /// <summary>Current envelope schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Hard per-case limit for the serialized snapshot document.</summary>
    public const int MaxSerializedBytes = 5 * 1024 * 1024;

    /// <summary>Files the reviewer will actually see, excluding explicitly omitted entries.</summary>
    public IReadOnlyList<CodeReviewEvaluationSnapshotFile> IncludedFiles =>
        [.. Files.Where(file => !file.IsOmitted)];

    /// <summary>Number of changed files the snapshot omits (binary or missing patch).</summary>
    public int OmittedFileCount => Files.Count(file => file.IsOmitted);

    /// <summary>Number of changed files the reviewer will see.</summary>
    public int ChangedFileCount => Files.Count - OmittedFileCount;

    /// <summary>
    /// Projects the envelope onto the production snapshot record used by the review engine.
    /// </summary>
    /// <remarks>
    /// Developer feedback is always empty: evaluation never feeds live PR feedback into a
    /// reviewer. The reference content is never part of this projection either; it is passed
    /// only to the judge.
    /// </remarks>
    public CodeReviewPullRequestSnapshot ToPullRequestSnapshot() =>
        new(
            Title,
            Body,
            [.. IncludedFiles.Select(file => file.ToFileSnapshot())],
            Array.Empty<CodeReviewDeveloperFeedbackComment>()
        );

    /// <summary>Computes the SHA-256 of the canonical serialized form.</summary>
    public string ComputeSha256() =>
        Convert.ToHexStringLower(
            SHA256.HashData(CodeReviewEvaluationSnapshotSerializer.Serialize(this))
        );
}

/// <summary>
/// One changed file inside an evaluation snapshot envelope.
/// </summary>
/// <param name="Path">Repository-relative path.</param>
/// <param name="PreviousPath">Previous path for renamed or copied files.</param>
/// <param name="MutationState">How the pull request changed the file.</param>
/// <param name="Patch">Unified diff patch text. Empty when <paramref name="IsOmitted"/> is true.</param>
/// <param name="IsOmitted">True when the file is binary or the provider returned no patch.</param>
public sealed record CodeReviewEvaluationSnapshotFile(
    string Path,
    string? PreviousPath,
    CodeReviewFileMutationState MutationState,
    string Patch,
    bool IsOmitted
)
{
    /// <summary>Projects one file onto the production file snapshot.</summary>
    public CodeReviewFileSnapshot ToFileSnapshot() => new(Path, PreviousPath, MutationState, Patch);
}

/// <summary>
/// Canonical JSON serialization for evaluation snapshot envelopes.
/// </summary>
/// <remarks>
/// One shared options instance keeps the persisted artifact and the computed SHA-256 in
/// agreement. Records use property names, not compact wire keys, so stored artifacts stay
/// readable during incident analysis.
/// </remarks>
public static class CodeReviewEvaluationSnapshotSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>Serializes one snapshot envelope to canonical UTF-8 JSON bytes.</summary>
    public static byte[] Serialize(CodeReviewEvaluationSnapshot snapshot) =>
        JsonSerializer.SerializeToUtf8Bytes(snapshot, Options);

    /// <summary>Deserializes one snapshot envelope from stored artifact text.</summary>
    public static CodeReviewEvaluationSnapshot Deserialize(string json) =>
        JsonSerializer.Deserialize<CodeReviewEvaluationSnapshot>(json, Options)
        ?? throw new CodeReviewEvaluationArtifactIntegrityException(
            "The evaluation snapshot artifact could not be deserialized."
        );
}
