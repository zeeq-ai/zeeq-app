using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using ModelContextProtocol.Server;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Mcp.CodeReviews;

/// <summary>A finding disposition proposed by an MCP client.</summary>
public sealed record McpFindingTriageItem(
    [property: Description("Required; finding ref (`9d43c.F2`) or unique semantic ID.")]
        string FindingRef,
    [property: Description("Required; `false_positive`, `deferred`, or `clear`.")]
        string Disposition,
    [property: Description(
        "Required for `false_positive`/`deferred` (1-1000 chars); optional for `clear`."
    )]
        string? Reason
);

public sealed partial class CodeReviewMcpTools
{
    /// <summary>Proposes finding triage for human confirmation.</summary>
    [McpServerTool(
        Name = "triage_review_findings",
        Title = "Triage Review Findings",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false
    )]
    [Description(
        """
            Propose `false_positive` or `deferred` triage on review findings, or clear an unconfirmed proposal.
            Proposals require human confirmation; MCP cannot confirm or overwrite confirmed triages.

            <triage_review_findings.triggers>
            - User or agent verifies a finding is a false positive or intentionally deferred for this PR
            - Clearing a previously proposed triage
            </triage_review_findings.triggers>

            <triage_review_findings.flow>
            1. Verify the finding against the codebase and prepare a concise factual `reason`.
            2. Pass `items` (`findingRef` like `9d43c.F2` or unique semantic ID, `disposition`, `reason`) with either `reviewId` or (`reviewGroupId` + `ownerQualifiedRepoName`).
            3. Surface returned `confirmationUrl` links for human confirmation.
            </triage_review_findings.flow>

            Provide exactly one of `reviewId` or `reviewGroupId` (`ownerQualifiedRepoName` required with `reviewGroupId`).
            `disposition`: `false_positive`, `deferred`, or `clear`. `reason`: non-blank, max 1000 chars for `false_positive`/`deferred`.
            """
    )]
    public static async Task<string> TriageReviewFindings(
        ICodeReviewFindingTriageService service,
        CodeReviewRequestLinkFactory links,
        ClaimsPrincipal? user,
        [Description("Required; findings with requested disposition and reason.")]
            IReadOnlyList<McpFindingTriageItem> items,
        [Description("Optional; source review ID. Provide reviewId or reviewGroupId, not both.")]
            string? reviewId = null,
        [Description("Optional; chained review group ID. Requires ownerQualifiedRepoName.")]
            string? reviewGroupId = null,
        [Description("Required with reviewGroupId; owner/repo (e.g. zeeq-ai/zeeq-app).")]
            string? ownerQualifiedRepoName = null,
        [Description("Optional; stable agent session ID for attribution.")]
            string? agentSessionId = null,
        CancellationToken cancellationToken = default
    )
    {
        if (user is null)
            return "Active organization is required.";
        var (orgId, _, actorId) = user.AsZeeqMinimalIdentity();
        if (string.IsNullOrWhiteSpace(orgId))
            return "Active organization is required.";
        if (string.IsNullOrWhiteSpace(reviewId) == string.IsNullOrWhiteSpace(reviewGroupId))
            return "Provide exactly one of reviewId or reviewGroupId.";
        if (items is null || items.Count == 0)
            return "Provide at least one finding.";
        if (
            !string.IsNullOrWhiteSpace(reviewGroupId)
            && string.IsNullOrWhiteSpace(ownerQualifiedRepoName)
        )
            return "ownerQualifiedRepoName is required with reviewGroupId.";
        FindingTriageTarget target = !string.IsNullOrWhiteSpace(reviewId)
            ? new FindingTriageTarget.ByReview(reviewId.Trim())
            : new FindingTriageTarget.ByReviewGroup(
                reviewGroupId!.Trim(),
                ownerQualifiedRepoName!.Trim()
            );
        var results = new List<object>();
        foreach (var item in items)
        {
            var disposition = item.Disposition?.Trim().ToLowerInvariant();
            if (
                string.IsNullOrWhiteSpace(item.FindingRef)
                || disposition is not ("false_positive" or "deferred" or "clear")
            )
            {
                results.Add(
                    new
                    {
                        item.FindingRef,
                        status = "Invalid",
                        error = "Provide a findingRef and false_positive, deferred, or clear.",
                    }
                );
                continue;
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
                    target,
                    item.FindingRef.Trim(),
                    operation,
                    value,
                    item.Reason,
                    CodeReviewFindingTriageChannel.Mcp,
                    actorId,
                    AgentSessionId: agentSessionId
                ),
                cancellationToken
            );
            results.Add(
                outcome switch
                {
                    FindingTriageOutcome.Applied applied => new
                    {
                        item.FindingRef,
                        status = "Applied",
                        confirmationUrl = applied.Row.ApprovalState
                        == CodeReviewFindingApprovalState.Proposed
                            ? links.BuildTriageLink(orgId, applied.Row.Id)
                            : null,
                    } as object,
                    FindingTriageOutcome.NotFound missing => new
                    {
                        item.FindingRef,
                        status = "NotFound",
                        missing.Candidates,
                    },
                    FindingTriageOutcome.Ambiguous ambiguous => new
                    {
                        item.FindingRef,
                        status = "Ambiguous",
                        ambiguous.Candidates,
                    },
                    FindingTriageOutcome.Forbidden forbidden => new
                    {
                        item.FindingRef,
                        status = "Forbidden",
                        forbidden.Reason,
                    },
                    FindingTriageOutcome.Invalid invalid => new
                    {
                        item.FindingRef,
                        status = "Invalid",
                        invalid.Errors,
                    },
                    FindingTriageOutcome.IgnoredStale => new
                    {
                        item.FindingRef,
                        status = "IgnoredStale",
                    },
                }
            );
        }
        return JsonSerializer.Serialize(results);
    }
}
