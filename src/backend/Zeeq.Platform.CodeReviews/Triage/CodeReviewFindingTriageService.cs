using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Zeeq.Core.Common;
using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>Identifies review history within one organization for a triage request.</summary>
public closed record FindingTriageTarget
{
    /// <summary>One review by durable ID.</summary>
    public sealed record ByReview(string ReviewId, DateTimeOffset? CreatedAtUtc = null)
        : FindingTriageTarget;

    /// <summary>One mapped pull request.</summary>
    public sealed record ByPullRequest(string RepositoryId, int PullRequestNumber)
        : FindingTriageTarget;

    /// <summary>An existing triage row.</summary>
    public sealed record ByTriageId(string TriageId) : FindingTriageTarget;
}

/// <summary>A resolved candidate that callers can show when a reference is ambiguous.</summary>
public sealed record FindingTriageCandidate(
    string ReviewId,
    string Reference,
    string SemanticId,
    string Summary
);

/// <summary>Results that callers must handle for a finding triage request.</summary>
public closed record FindingTriageOutcome
{
    /// <summary>Applied or already identical.</summary>
    public sealed record Applied(CodeReviewFindingTriage Row) : FindingTriageOutcome;

    /// <summary>No matching finding.</summary>
    public sealed record NotFound(IReadOnlyList<FindingTriageCandidate> Candidates)
        : FindingTriageOutcome;

    /// <summary>More than one matching finding.</summary>
    public sealed record Ambiguous(IReadOnlyList<FindingTriageCandidate> Candidates)
        : FindingTriageOutcome;

    /// <summary>The channel lacks authority.</summary>
    public sealed record Forbidden(string Reason) : FindingTriageOutcome;

    /// <summary>A source event predates the current watermark.</summary>
    public sealed record IgnoredStale(CodeReviewFindingTriage Row) : FindingTriageOutcome;

    /// <summary>The requested operation is invalid.</summary>
    public sealed record Invalid(IReadOnlyList<string> Errors) : FindingTriageOutcome;
}

/// <summary>One authorized request to mutate a finding disposition.</summary>
public sealed record FindingTriageRequest(
    string OrganizationId,
    FindingTriageTarget Target,
    string FindingReference,
    FindingTriageOperation Operation,
    CodeReviewFindingDisposition? Disposition,
    string? Reason,
    CodeReviewFindingTriageChannel Channel,
    string? ActorUserId,
    DateTimeOffset? SourceEventAtUtc = null,
    CodeReviewFindingDisposition? ExpectedDisposition = null,
    string? AgentSessionId = null,
    string? SourceUrl = null,
    string? GitHubAliasId = null,
    string? ConfirmationQuote = null,
    string? AuthMode = null,
    string? ClientId = null
);

/// <summary>Resolves review findings and performs guarded triage transitions.</summary>
public interface ICodeReviewFindingTriageService
{
    /// <summary>Resolve the requested finding and apply the transition.</summary>
    Task<FindingTriageOutcome> TriageAsync(
        FindingTriageRequest request,
        CancellationToken cancellationToken = default
    );
}

/// <summary>Shared triage entry point for human and agent channels.</summary>
internal sealed partial class CodeReviewFindingTriageService(
    ICodeReviewRecordStore reviews,
    ICodeReviewArtifactStore artifacts,
    CodeReviewXmlOutputValidator validator,
    ICodeReviewFindingTriageStore triages,
    ILogger<CodeReviewFindingTriageService>? logger = null
) : ICodeReviewFindingTriageService
{
    private static readonly System.Diagnostics.Metrics.Counter<long> TriageCounter =
        ZeeqTelemetry.Metrics.CreateCounter<long>("zeeq_code_review_triages_total");

    public async Task<FindingTriageOutcome> TriageAsync(
        FindingTriageRequest request,
        CancellationToken cancellationToken = default
    )
    {
        using var activity = ZeeqTelemetry.Trace(
            [
                ("organization.id", request.OrganizationId),
                ("triage.channel", request.Channel.ToString()),
            ],
            "code_reviews.triage.write"
        );
        var outcome = await TriageCoreAsync(request, cancellationToken);
        var outcomeName = outcome.GetType().Name;
        activity?.SetTag("triage.outcome", outcomeName);
        var row = outcome switch
        {
            FindingTriageOutcome.Applied x => x.Row,
            FindingTriageOutcome.IgnoredStale x => x.Row,
            _ => null,
        };
        activity?.SetTag("code_review.id", row?.SourceReviewId);
        activity?.SetTag("triage.occurrence_key", row?.OccurrenceKey);
        activity?.SetTag("triage.disposition", row?.Disposition?.ToString());
        activity?.SetTag("triage.approval_state", row?.ApprovalState.ToString());
        TriageCounter.Add(
            1,
            new("channel", request.Channel.ToString()),
            new("disposition", row?.Disposition?.ToString()),
            new("approval_state", row?.ApprovalState.ToString()),
            new("outcome", outcomeName)
        );
        return outcome;
    }

    private async Task<FindingTriageOutcome> TriageCoreAsync(
        FindingTriageRequest request,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(request.OrganizationId))
            return new FindingTriageOutcome.Invalid(["Organization is required."]);
        if (request.Target is FindingTriageTarget.ByTriageId byId)
        {
            var row = await triages.FindAsync(
                request.OrganizationId,
                byId.TriageId,
                cancellationToken
            );
            return row is null
                ? new FindingTriageOutcome.NotFound([])
                : await WriteAsync(row, request, cancellationToken);
        }
        var history = await reviews.ListForTriageAsync(
            request.OrganizationId,
            request.Target,
            cancellationToken
        );
        if (history.Count == 0)
            return new FindingTriageOutcome.NotFound([]);
        var findingReference = request.FindingReference ?? string.Empty;
        var isReference = Regex.IsMatch(
            findingReference,
            "^[a-fA-F0-9]{5,}\\.F[1-9][0-9]*$",
            RegexOptions.CultureInvariant
        );
        var isBareReference =
            request.Target is FindingTriageTarget.ByReview
            && Regex.IsMatch(
                findingReference,
                "^F[1-9][0-9]*$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            );
        if (
            isReference
            && request.Target is FindingTriageTarget.ByReview byReview
            && !string.Equals(
                CodeReviewFindingIds.FormatShortReference(byReview.ReviewId, "F1").Split('.')[0],
                findingReference.Split('.')[0],
                StringComparison.OrdinalIgnoreCase
            )
        )
            return new FindingTriageOutcome.Invalid(["findingRef belongs to a different review."]);
        var candidates = new List<FindingTriageCandidate>();
        // A short reference identifies the review tail before any artifact is loaded.
        var selectedReviews = isReference
            ? history.Where(x =>
                string.Equals(
                    CodeReviewFindingIds.FormatShortReference(x.Id, "F1").Split('.')[0],
                    findingReference.Split('.')[0],
                    StringComparison.OrdinalIgnoreCase
                )
            )
            : history.Take(1);
        var matches =
            new List<(
                CodeReviewRecord Review,
                IndexedCodeReviewFinding Finding,
                FindingTriageCandidate Candidate
            )>();
        foreach (var review in selectedReviews)
        {
            if (string.IsNullOrEmpty(review.FindingsStorageUri))
                continue;
            await using var stream = await artifacts.OpenFindingsAsync(
                review.FindingsStorageUri,
                cancellationToken
            );
            using var reader = new StreamReader(stream);
            var validation = validator.Validate(await reader.ReadToEndAsync(cancellationToken));
            if (!validation.IsValid || validation.Output is null)
                continue;
            foreach (var finding in CodeReviewFindingIds.Assign(validation.Output))
            {
                var reference = CodeReviewFindingIds.FormatShortReference(
                    review.Id,
                    finding.OccurrenceKey
                );
                if (candidates.Count < 10)
                    candidates.Add(
                        new(review.Id, reference, finding.SemanticId, finding.Finding.Summary)
                    );
                if (
                    !(
                        isBareReference
                            ? string.Equals(
                                finding.OccurrenceKey,
                                findingReference,
                                StringComparison.OrdinalIgnoreCase
                            )
                        : isReference
                            ? string.Equals(
                                reference,
                                request.FindingReference,
                                StringComparison.OrdinalIgnoreCase
                            )
                        : string.Equals(
                            finding.SemanticId,
                            request.FindingReference,
                            StringComparison.Ordinal
                        )
                    )
                )
                    continue;
                matches.Add(
                    (
                        review,
                        finding,
                        new(review.Id, reference, finding.SemanticId, finding.Finding.Summary)
                    )
                );
            }
        }
        if (matches.Count == 0)
            return new FindingTriageOutcome.NotFound(candidates);
        if (matches.Count > 1)
            return new FindingTriageOutcome.Ambiguous(matches.Select(x => x.Candidate).ToArray());
        var (source, indexed, _) = matches[0];
        var findingOutput = indexed.Finding;
        var snapshot = new CodeReviewFindingTriage
        {
            Id = $"cft_{Guid.CreateVersion7():N}",
            OrganizationId = request.OrganizationId,
            TeamId = source.TeamId,
            RepositoryId = source.RepositoryId,
            OwnerQualifiedRepoName = source.OwnerQualifiedRepoName.ToLowerInvariant(),
            PullRequestNumber = source.PullRequestNumber > 0 ? source.PullRequestNumber : null,
            ReviewGroupId = source.ReviewGroupId,
            SourceReviewId = source.Id,
            SourceReviewCreatedAtUtc = source.CreatedAtUtc,
            OccurrenceKey = indexed.OccurrenceKey,
            SemanticId = indexed.SemanticId,
            Facet = indexed.Facet.Facet,
            Severity = findingOutput.Level,
            FilePath = string.IsNullOrEmpty(findingOutput.File) ? null : findingOutput.File,
            LineNumber = findingOutput.Line > 0 ? findingOutput.Line : null,
            FindingSummary = findingOutput.Summary,
        };
        return await WriteAsync(snapshot, request, cancellationToken);
    }

    private async Task<FindingTriageOutcome> WriteAsync(
        CodeReviewFindingTriage snapshot,
        FindingTriageRequest request,
        CancellationToken cancellationToken
    )
    {
        var result = await triages.UpsertLwwAsync(
            new(
                snapshot,
                request.Operation,
                request.Disposition,
                request.Reason,
                request.Channel,
                request.ActorUserId,
                SourceEventAtUtc: request.SourceEventAtUtc,
                ExpectedDisposition: request.ExpectedDisposition,
                AgentSessionId: request.AgentSessionId,
                SourceUrl: request.SourceUrl,
                ConfirmationQuote: request.ConfirmationQuote,
                AuthMode: request.AuthMode,
                ClientId: request.ClientId
            ),
            cancellationToken
        );
        if (result.Changed && result.Row is { } row && logger is not null)
        {
            LogMutation(
                logger,
                SanitizeLogValue(request.OrganizationId)!,
                SanitizeLogValue(row.Id)!,
                SanitizeLogValue(row.SourceReviewId)!,
                SanitizeLogValue(row.OccurrenceKey)!,
                SanitizeLogValue(row.SemanticId)!,
                request.Channel.ToString(),
                SanitizeLogValue(request.ActorUserId),
                SanitizeLogValue(request.GitHubAliasId),
                SanitizeLogValue(request.AgentSessionId),
                result.PreviousDisposition?.ToString(),
                result.PreviousApprovalState?.ToString(),
                row.Disposition?.ToString(),
                row.ApprovalState.ToString(),
                row.CurrentReason?.Length ?? 0
            );
        }
        return result.Status switch
        {
            FindingTriageWriteStatus.Applied => new FindingTriageOutcome.Applied(result.Row!),
            FindingTriageWriteStatus.Forbidden => new FindingTriageOutcome.Forbidden(
                result.Error ?? "Forbidden"
            ),
            FindingTriageWriteStatus.IgnoredStale => new FindingTriageOutcome.IgnoredStale(
                result.Row!
            ),
            FindingTriageWriteStatus.Invalid => new FindingTriageOutcome.Invalid([
                result.Error ?? "Invalid transition",
            ]),
            _ => throw new InvalidOperationException("Unknown triage result."),
        };
    }

    private static string? SanitizeLogValue(string? value) =>
        value
            ?.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Triage changed OrganizationId={OrganizationId} TriageId={TriageId} SourceReviewId={SourceReviewId} OccurrenceKey={OccurrenceKey} SemanticId={SemanticId} Channel={Channel} ActorUserId={ActorUserId} GitHubAliasId={GitHubAliasId} AgentSessionId={AgentSessionId} PreviousDisposition={PreviousDisposition} PreviousApprovalState={PreviousApprovalState} NewDisposition={NewDisposition} NewApprovalState={NewApprovalState} ReasonLength={ReasonLength}"
    )]
    private static partial void LogMutation(
        ILogger logger,
        string organizationId,
        string triageId,
        string sourceReviewId,
        string occurrenceKey,
        string semanticId,
        string channel,
        string? actorUserId,
        string? gitHubAliasId,
        string? agentSessionId,
        string? previousDisposition,
        string? previousApprovalState,
        string? newDisposition,
        string newApprovalState,
        int reasonLength
    );
}
