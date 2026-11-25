using System.Xml.Linq;
using Zeeq.Core.Carts;
using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Platform.Carts;

/// <summary>
/// Compiles saved cart items into the agent-ready XML instructions block.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shared output.</b>  This formatter is the single source of truth for the compiled
/// cart format.  Both <c>GET /carts/{id}/text</c> and the MCP <c>get_cart_findings</c>
/// tool call <see cref="ToAgentInstructions"/> — output is always identical regardless
/// of which path the agent uses.
/// </para>
/// <para>
/// <b>XML shape.</b>  The output mirrors the existing code-review findings XML format
/// (see <c>CodeReviewFacetTabs.vue</c>'s <c>formatReviewerSectionGuidance</c>), extended
/// with per-finding <c>repo</c>, <c>pr</c>, and optional <c>note</c> attributes for
/// provenance and reviewer annotations.
/// </para>
/// </remarks>
public static class CartInstructionsTextBuilder
{
    /// <summary>Loads current triage state and renders findings with live annotations.</summary>
    public static async Task<string> ToAgentInstructionsAsync(
        IReadOnlyList<CartFindingSnapshot> items,
        string organizationId,
        ICodeReviewFindingTriageStore triages,
        CodeReviewRequestLinkFactory links,
        bool actionableOnly = false,
        CancellationToken cancellationToken = default
    )
    {
        var reviewIds = items
            .Select(x => x.SourceReviewId)
            .Where(x => !string.IsNullOrEmpty(x))
            .Distinct()
            .ToArray();
        var loaded = await Task.WhenAll(
            reviewIds.Select(id =>
                triages.ListForReviewAsync(organizationId, id!, cancellationToken)
            )
        );
        var rows = loaded.SelectMany(x => x).ToDictionary(x => (x.SourceReviewId, x.OccurrenceKey));
        var actionable = new List<XElement>();
        var triaged = new List<XElement>();
        var pending = 0;
        var confirmedCount = 0;
        var open = 0;
        foreach (var item in items)
        {
            CodeReviewFindingTriage? row = null;
            if (item.SourceReviewId is not null && item.OccurrenceKey is not null)
                rows.TryGetValue((item.SourceReviewId, item.OccurrenceKey), out row);
            var confirmed =
                row?.ApprovalState == CodeReviewFindingApprovalState.Confirmed
                && row.Disposition is not null;
            // Only a human-confirmed decision removes a finding from actionable exports.
            var isTriaged = confirmed;
            if (
                row?.ApprovalState == CodeReviewFindingApprovalState.Proposed
                && row.Disposition is not null
            )
                pending++;
            else if (confirmed)
                confirmedCount++;
            else
                open++;
            if (actionableOnly && isTriaged)
                continue;
            var element = XElement.Parse(item.FormatFinding());
            if (item.SourceReviewId is not null)
                element.SetAttributeValue("reviewId", item.SourceReviewId);
            if (item.SourceReviewId is not null && item.OccurrenceKey is not null)
                element.SetAttributeValue(
                    "ref",
                    CodeReviewFindingIds.FormatShortReference(
                        item.SourceReviewId,
                        item.OccurrenceKey
                    )
                );
            if (item.SemanticId is not null)
                element.SetAttributeValue("semanticId", item.SemanticId);
            if (row?.Disposition is not null)
            {
                element.SetAttributeValue("disposition", row.Disposition.ToString());
                element.SetAttributeValue("approval", row.ApprovalState.ToString());
                element.SetAttributeValue(
                    "confirmationUrl",
                    links.BuildTriageLink(organizationId, row.Id)
                );
                element.Add(new XElement("triage_reason", row.CurrentReason));
            }
            (isTriaged ? triaged : actionable).Add(element);
        }
        var document = XDocument.Parse(items.ToAgentInstructions());
        document.Root!.ReplaceWith(
            new XElement(
                "review",
                new XAttribute("open", open),
                new XAttribute("pending", pending),
                new XAttribute("triaged", confirmedCount),
                new XElement(
                    "triage_summary",
                    $"{open} open · {pending} pending confirmation · {confirmedCount} triaged"
                ),
                new XElement("findings", actionable),
                new XElement("triaged_findings", triaged)
            )
        );
        return document.ToString();
    }

    extension(IReadOnlyList<CartFindingSnapshot> items)
    {
        /// <summary>
        /// Converts the list of saved findings into the agent-ready XML instructions
        /// block.
        /// </summary>
        /// <returns>
        /// A string which contains instructions as well as a payload for the agent
        /// to work from.
        /// </returns>
        public string ToAgentInstructions()
        {
            var findings = items.Count is 0
                ? ""
                : Environment.NewLine
                    + string.Join(Environment.NewLine, items.Select(item => item.FormatFinding()));

            return $$"""
                <!--
                <instruction_for_agents>
                    The following XML is the raw output from expert code reviewers analyzing the PR.
                    <review_steps>
                    1. Review each finding from expert reviewers
                    2. Evaluate veracity and validity of each finding against the broader context of the codebase, feature, and work
                    3. Confirm decisions with the user and call triage_review_findings with a reason for false positives or deferred findings. Add a // NOTE: only when the code needs an explanation.
                    </review_steps>
                    <process>
                    1. Findings and feedback are high level; plan out specific code changes needed finding-by-finding; ground in actual code
                    2. Clear explanation of the identified finding and explain tradeoffs, shortcomings, different angles in the reviewers findings if any (especially with grounding and broader context of the codebase)
                    3. Present the concrete and specific changes needed; user decides which to proceed with; **do not make changes without confirmation**
                    4. For verified findings, present multiple options for resolution as a pick list (pick what to fix + pick options you present) with a reasonable recommended default and explanation why
                    5. ALWAYS ensure there is enough clarity to make the best fix if there is ambiguity or insufficient feedback to confidently implement the change
                    6. ALWAYS get confirmation and acceptance of the proposed fix for each finding before making changes
                    </process>
                    <important>
                    The user_annotation (when present) is a DIRECT NOTE from the reviewer on how to handle the finding; give weight to this instruction when present
                    </important>
                </instruction_for_agents>
                -->

                <review>
                  <findings>
                    {{findings}}
                  </findings>
                </review>

                """;
        }
    }

    extension(string cartId)
    {
        /// <summary>
        /// Returns the short MCP invocation instruction a user pastes into their agent to
        /// trigger a <c>get_cart_findings</c> tool call.  The full findings XML is returned
        /// by the tool itself, keeping the copied text small.
        /// </summary>
        public string ToMcpInstructions() =>
            $"""
                Use the Zeeq MCP tool get_cart_findings; retrieve code review findings using cartId={cartId}.
                """;
    }

    extension(CartFindingSnapshot item)
    {
        private string FormatFinding()
        {
            var element = new XElement(
                "finding",
                new XAttribute("hash", item.Hash),
                new XAttribute("file", item.File),
                item.Line is { } line ? new XAttribute("line", line) : null,
                item.Side is { } side ? new XAttribute("side", side) : null,
                new XAttribute("criticality", item.Criticality),
                new XAttribute("facet", item.Facet),
                new XAttribute("agent", item.Agent),
                item.Annotation is { } annotation ? new XAttribute("note", annotation) : null,
                new XAttribute("repo", item.OwnerQualifiedRepoName),
                new XAttribute("pr", item.PullRequestNumber),
                new XElement("title", item.Title),
                new XElement("summary", item.Summary),
                new XElement("body", BuildBodyCData(item.Body)),
                new XElement("user_annotation", item.Annotation)
            );

            return element.ToString(SaveOptions.None);
        }
    }

    /// <summary>
    /// Splits <paramref name="value"/> on any embedded <c>]]&gt;</c> sequences so the
    /// resulting <see cref="XCData"/> sections can never terminate early — <see cref="XCData"/>
    /// writes its content verbatim and does not escape that sequence itself.
    /// </summary>
    private static IEnumerable<XCData> BuildBodyCData(string value)
    {
        var parts = value.Split("]]>", StringSplitOptions.None);

        for (var i = 0; i < parts.Length; i++)
        {
            var prefix = i is 0 ? "" : ">";
            var suffix = i == parts.Length - 1 ? "" : "]]";
            yield return new XCData(prefix + parts[i] + suffix);
        }
    }
}
