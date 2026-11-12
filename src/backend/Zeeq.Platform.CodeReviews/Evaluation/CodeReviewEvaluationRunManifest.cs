using System.Text.Json;
using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Immutable input manifest copied at run admission.
/// </summary>
/// <remarks>
/// Every mutable input is copied by content, not by reference or hash alone: reference
/// findings, reviewer configuration, effective repository configuration and library ids
/// all live here. Later dataset edits, agent edits, tier mapping changes or shared prompt
/// edits cannot relabel an admitted run. The manifest is stored as one jsonb document on
/// the run row and is the authoritative case list for results, retries and reporting.
/// </remarks>
/// <param name="SchemaVersion">Manifest schema version.</param>
/// <param name="OrganizationId">Organization that owns the run.</param>
/// <param name="RepositoryId">Repository mapping evaluated by the run.</param>
/// <param name="DatasetId">Dataset the manifest was copied from.</param>
/// <param name="DatasetVersion">Dataset version copied into the manifest.</param>
/// <param name="AgentId">Reviewer agent evaluated by the run.</param>
/// <param name="ReviewerDisplayName">Reviewer name at admission.</param>
/// <param name="ReviewerFacet">Reviewer facet at admission.</param>
/// <param name="SavedRevisionId">Saved revision the reviewer configuration came from, when any.</param>
/// <param name="ReviewerConfiguration">Full reviewer configuration content at admission.</param>
/// <param name="ReviewerSelection">Frozen reviewer model descriptor.</param>
/// <param name="JudgeSelection">Frozen judge model descriptor.</param>
/// <param name="JudgeRubricVersion">Judge rubric version captured at admission.</param>
/// <param name="SharedSystemPrompt">Exact shared system prompt text admitted for this run.</param>
/// <param name="SharedSystemPromptHash">SHA-256 of <paramref name="SharedSystemPrompt"/>.</param>
/// <param name="RepositoryConfigurationJson">Effective repository review configuration at admission.</param>
/// <param name="LibraryIds">Library ids mapped to the repository at admission.</param>
/// <param name="AdmittedBuildVersion">Deployed build identity recorded at admission.</param>
/// <param name="AdmittedSystemPromptVersion">System prompt version recorded at admission.</param>
/// <param name="RequestedByUserId">User that requested the run.</param>
/// <param name="RequestFingerprint">
/// Canonical hash of the submitted request, excluding the idempotency key. Distinguishes a
/// saved-revision run from a draft run, and detects a changed dataset version, revision or
/// draft content reusing the same key. Duplicate submissions compare this value.
/// </param>
/// <param name="AdmittedAtUtc">UTC time the manifest was frozen.</param>
/// <param name="ParentRunId">Parent run for a failed-case retry, when any.</param>
/// <param name="Cases">Frozen case list. The manifest is authoritative for results.</param>
public sealed record CodeReviewEvaluationRunManifest(
    int SchemaVersion,
    string OrganizationId,
    string RepositoryId,
    string DatasetId,
    long DatasetVersion,
    string AgentId,
    string ReviewerDisplayName,
    string ReviewerFacet,
    string? SavedRevisionId,
    CodeReviewerAgentRevisionConfiguration ReviewerConfiguration,
    CodeReviewEvaluationModelSelection ReviewerSelection,
    CodeReviewEvaluationModelSelection JudgeSelection,
    string JudgeRubricVersion,
    string SharedSystemPrompt,
    string SharedSystemPromptHash,
    string RepositoryConfigurationJson,
    IReadOnlyList<string> LibraryIds,
    string AdmittedBuildVersion,
    string AdmittedSystemPromptVersion,
    string RequestedByUserId,
    string RequestFingerprint,
    DateTimeOffset AdmittedAtUtc,
    string? ParentRunId,
    IReadOnlyList<CodeReviewEvaluationManifestCase> Cases
)
{
    /// <summary>Current manifest schema version.</summary>
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// One case frozen into a run manifest.
/// </summary>
/// <param name="CaseId">Dataset case identity at admission. Results key off this value.</param>
/// <param name="PullRequestNumber">Provider pull request number.</param>
/// <param name="Title">Pull request title captured with the snapshot. Historically stable.</param>
/// <param name="Snapshot">Immutable snapshot artifact reference.</param>
/// <param name="BaseSha">Captured base commit identity.</param>
/// <param name="HeadSha">Captured head commit identity.</param>
/// <param name="CapturedAtUtc">Capture time recorded on the dataset case.</param>
/// <param name="ChangedFileCount">Changed files visible to the reviewer at capture.</param>
/// <param name="OmittedFileCount">Changed files omitted from the snapshot at capture.</param>
/// <param name="References">This reviewer's frozen reference set for the case.</param>
public sealed record CodeReviewEvaluationManifestCase(
    string CaseId,
    int PullRequestNumber,
    string Title,
    CodeReviewEvaluationArtifact Snapshot,
    string BaseSha,
    string HeadSha,
    DateTimeOffset CapturedAtUtc,
    int ChangedFileCount,
    int OmittedFileCount,
    CodeReviewEvaluationReferenceSet References
);

/// <summary>
/// Frozen, non-secret model descriptor for one run stage.
/// </summary>
/// <remarks>
/// The worker builds the provider client from this descriptor instead of rereading tier
/// mappings, so a mapping change during a run cannot relabel execution. The credential
/// source is typed and distinguishes the system default, the internal default key and an
/// organization-managed key; the two defaults must never fall back to each other. Only the
/// organization key id is stored, never key material, and this descriptor is excluded from
/// UI provenance DTOs and ordinary logs.
/// </remarks>
/// <param name="Tier">Semantic tier requested by the run.</param>
/// <param name="Provider">Resolved provider id.</param>
/// <param name="Model">Resolved model id.</param>
/// <param name="Endpoint">Resolved endpoint or deployment name, when the provider needs one.</param>
/// <param name="CredentialSource">Which credential construction path the worker must use.</param>
/// <param name="OrganizationKeyId">Organization-owned key id reference, only for <see cref="CodeReviewEvaluationCredentialSource.OrganizationManagedKey"/>.</param>
public sealed record CodeReviewEvaluationModelSelection(
    CodeReviewModelTier Tier,
    string Provider,
    string Model,
    string? Endpoint,
    CodeReviewEvaluationCredentialSource CredentialSource,
    string? OrganizationKeyId
)
{
    /// <summary>
    /// Redacted rendering that never exposes the organization key reference.
    /// </summary>
    /// <remarks>
    /// Record default <c>ToString</c> prints every positional member, so an accidental
    /// interpolation into a log line would leak the key id. This override keeps diagnostics
    /// useful without the reference.
    /// </remarks>
    public override string ToString() =>
        $"CodeReviewEvaluationModelSelection {{ Tier = {Tier}, Provider = {Provider}, Model = {Model}, "
        + $"Endpoint = {Endpoint ?? "<none>"}, CredentialSource = {CredentialSource}, "
        + $"OrganizationKeyId = {(OrganizationKeyId is null ? "<none>" : "<redacted>")} }}";
}

/// <summary>
/// Credential construction path frozen with a model descriptor.
/// </summary>
public enum CodeReviewEvaluationCredentialSource
{
    /// <summary>System-level provider default credentials.</summary>
    SystemDefault,

    /// <summary>Internal default key configured for the deployment.</summary>
    InternalDefaultKey,

    /// <summary>Organization-managed key referenced by id.</summary>
    OrganizationManagedKey,
}

/// <summary>
/// Aggregate counts recorded on a run and returned to the UI.
/// </summary>
/// <param name="CaseCount">Manifest case count.</param>
/// <param name="GradedCaseCount">Cases that reached a successful binary grade.</param>
/// <param name="SkippedCaseCount">Cases skipped before any provider call.</param>
/// <param name="FailedCaseCount">Cases that failed the reviewer or judge stage.</param>
/// <param name="UnlabelledCaseCount">Cases with a reviewer reference set that has no expectation.</param>
/// <param name="MatchedReferenceCount">Expected findings the judge matched.</param>
/// <param name="MissedReferenceCount">Expected findings the judge reported as missed.</param>
/// <param name="ExpectedReferenceCount">Expected findings across graded labelled cases.</param>
/// <param name="AdditionalFindingCount">Candidate findings reported as additional and unverified.</param>
public sealed record CodeReviewEvaluationRunCounts(
    int CaseCount,
    int GradedCaseCount,
    int SkippedCaseCount,
    int FailedCaseCount,
    int UnlabelledCaseCount,
    int MatchedReferenceCount,
    int MissedReferenceCount,
    int ExpectedReferenceCount,
    int AdditionalFindingCount
);

/// <summary>
/// Canonical JSON serialization for evaluation run manifests.
/// </summary>
public static class CodeReviewEvaluationRunManifestSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>Serializes a manifest for jsonb storage.</summary>
    public static string Serialize(CodeReviewEvaluationRunManifest manifest) =>
        JsonSerializer.Serialize(manifest, Options);

    /// <summary>Deserializes a stored manifest.</summary>
    public static CodeReviewEvaluationRunManifest Deserialize(string json) =>
        JsonSerializer.Deserialize<CodeReviewEvaluationRunManifest>(json, Options)
        ?? throw new InvalidOperationException("The evaluation run manifest could not be read.");
}
