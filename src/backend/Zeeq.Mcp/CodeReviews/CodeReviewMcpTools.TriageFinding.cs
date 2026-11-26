using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using ModelContextProtocol.Server;
using OpenIddict.Abstractions;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Mcp.CodeReviews;

public sealed partial class CodeReviewMcpTools
{
    // NOTE: A single finding and required reviewId keep the schema flat (four required scalars)
    // and let mixed-review carts use parallel calls. Findings live only in immutable review XML,
    // so resolve server-side to catch typos and obtain authoritative OccurrenceKey, SemanticId,
    // Facet, Severity, FilePath and FindingSummary. If telemetry shows blob-read contention,
    // cache parsed Assign() output in IMemoryCache keyed by immutable FindingsStorageUri.
    /// <summary>Proposes one finding disposition for human confirmation.</summary>
    [McpServerTool(
        Name = "triage_review_finding",
        Title = "Triage Review Finding",
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false
    )]
    [Description(
        """
            After a review, propose finding triage for human confirmation or clear an unconfirmed proposal.
            <triage_review_finding.triggers>
            - User or agent verifies a finding is a false positive or intentionally deferred.
            - Clear a previous proposal.
            </triage_review_finding.triggers>
            <triage_review_finding.flow>
            1. Verify the finding and prepare a factual reason.
            2. Pass reviewId, findingRef, disposition and reason.
            3. For a proposal, surface the confirmationUrl for human confirmation.
            4. Only after the user explicitly confirms this specific finding and disposition in the session, optionally pass confirmed=true and their exact words as confirmationQuote. Never invent or infer confirmation.
            5. If a finding was reopened after an agent attestation, use web confirmation for any later decision.
            </triage_review_finding.flow>
            """
    )]
    public static async Task<string> TriageReviewFinding(
        ICodeReviewFindingTriageService service,
        CodeReviewRequestLinkFactory links,
        CodeReviewAuthorization authorization,
        ClaimsPrincipal? user,
        [Description("Required; review ID containing the finding (`reviewId` in review output).")]
            string reviewId,
        [Description("Required; finding ref (`9d43c.F2` or `F2`) or unique semantic ID.")]
            string findingRef,
        [Description("Required; `false_positive`, `deferred`, or `clear`.")] string disposition,
        [Description(
            "Required for `false_positive`/`deferred` (1-1000 chars); optional for `clear`."
        )]
            string? reason = null,
        [Description("Optional; stable agent session ID for attribution.")]
            string? agentSessionId = null,
        [Description(
            "Set true only after the user explicitly confirms this specific finding and disposition in this session."
        )]
            bool confirmed = false,
        [Description(
            "Required with confirmed=true: exact user confirmation, 1-500 characters; agent-reported and not independently verified."
        )]
            string? confirmationQuote = null,
        CancellationToken cancellationToken = default
    )
    {
        if (user is null)
            return "Active organization is required.";
        var (orgId, _, actorId) = user.AsZeeqMinimalIdentity();
        if (string.IsNullOrWhiteSpace(orgId))
            return "Active organization is required.";
        disposition = disposition?.Trim().ToLowerInvariant() ?? "";
        if (
            string.IsNullOrWhiteSpace(reviewId)
            || string.IsNullOrWhiteSpace(findingRef)
            || disposition is not ("false_positive" or "deferred" or "clear")
        )
            return JsonSerializer.Serialize(
                new
                {
                    findingRef,
                    status = "Invalid",
                    error = "Provide reviewId, findingRef and false_positive, deferred, or clear.",
                }
            );
        if (confirmed)
        {
            if (
                disposition == "clear"
                || string.IsNullOrWhiteSpace(confirmationQuote)
                || confirmationQuote.Length > 500
                || confirmationQuote.Contains('\0')
                || reason?.Contains('\0') == true
                || agentSessionId?.Contains('\0') == true
            )
                return JsonSerializer.Serialize(
                    new
                    {
                        findingRef,
                        status = "Invalid",
                        error = "Confirmed triage requires a nonblank exact confirmationQuote of at most 500 characters, valid text, and a disposition.",
                    }
                );
            if (agentSessionId?.Length > 128)
                return JsonSerializer.Serialize(
                    new
                    {
                        findingRef,
                        status = "Invalid",
                        error = "agentSessionId exceeds 128 characters.",
                    }
                );
            if (
                user.Identity?.IsAuthenticated != true
                || user.FindFirst(AuthClaims.AuthMode)?.Value
                    == OpenIddictConstants.GrantTypes.ClientCredentials
                || string.IsNullOrWhiteSpace(actorId)
                || await authorization.ResolveAsync(orgId, user, cancellationToken) is null
            )
                return JsonSerializer.Serialize(
                    new
                    {
                        findingRef,
                        status = "Forbidden",
                        error = "An authenticated organization member is required for agent-attested triage.",
                    }
                );
        }
        var operation =
            disposition == "clear" ? FindingTriageOperation.Clear : FindingTriageOperation.Set;
        CodeReviewFindingDisposition? value = disposition switch
        {
            "false_positive" => CodeReviewFindingDisposition.FalsePositive,
            "deferred" => CodeReviewFindingDisposition.Deferred,
            _ => null,
        };
        var outcome = await service.TriageAsync(
            new FindingTriageRequest(
                orgId,
                new FindingTriageTarget.ByReview(reviewId.Trim()),
                findingRef.Trim(),
                operation,
                value,
                reason,
                confirmed
                    ? CodeReviewFindingTriageChannel.AgentAttested
                    : CodeReviewFindingTriageChannel.Mcp,
                actorId,
                AgentSessionId: agentSessionId,
                ConfirmationQuote: confirmed ? confirmationQuote : null,
                AuthMode: confirmed ? user.FindFirst(AuthClaims.AuthMode)?.Value : null,
                ClientId: confirmed ? user.FindFirst("azp")?.Value : null
            ),
            cancellationToken
        );
        object result = outcome switch
        {
            FindingTriageOutcome.Applied applied => new
            {
                findingRef,
                status = "Applied",
                confirmationUrl = applied.Row.ApprovalState
                == CodeReviewFindingApprovalState.Proposed
                    ? links.BuildTriageLink(orgId, applied.Row.Id)
                    : null,
            },
            FindingTriageOutcome.NotFound missing => new
            {
                findingRef,
                status = "NotFound",
                candidates = missing.Candidates,
            },
            FindingTriageOutcome.Ambiguous ambiguous => new
            {
                findingRef,
                status = "Ambiguous",
                candidates = ambiguous.Candidates,
            },
            FindingTriageOutcome.Forbidden forbidden => new
            {
                findingRef,
                status = "Forbidden",
                error = forbidden.Reason,
            },
            FindingTriageOutcome.Invalid invalid => new
            {
                findingRef,
                status = "Invalid",
                error = string.Join("; ", invalid.Errors),
            },
            FindingTriageOutcome.IgnoredStale => new { findingRef, status = "IgnoredStale" },
        };
        return JsonSerializer.Serialize(result);
    }
}
