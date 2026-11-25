using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using ModelContextProtocol.Server;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Mcp.CodeReviews;

/// <summary>A finding disposition proposed by an MCP client.</summary>
public sealed record McpFindingTriageItem(string FindingRef, string Disposition, string? Reason);

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
        "Propose false_positive or deferred decisions, or clear a proposal. Provide reviewId or reviewGroupId and ownerQualifiedRepoName, and finding references with reasons. A human must confirm proposals."
    )]
    public static async Task<string> TriageReviewFindings(
        ICodeReviewFindingTriageService service,
        CodeReviewRequestLinkFactory links,
        ClaimsPrincipal? user,
        [Description("Findings and their requested dispositions.")]
            IReadOnlyList<McpFindingTriageItem> items,
        [Description("Source review ID, or use reviewGroupId.")] string? reviewId = null,
        [Description("Review group ID, or use reviewId.")] string? reviewGroupId = null,
        [Description("Repository owner/name, required with reviewGroupId.")]
            string? ownerQualifiedRepoName = null,
        [Description("Optional agent session ID.")] string? agentSessionId = null,
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
