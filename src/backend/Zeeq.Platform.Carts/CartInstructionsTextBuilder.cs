using System.Xml.Linq;
using Zeeq.Core.Carts;

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
                    3. For expected behaviors, suggest leaving a comment in the code to explain the rationale to future travelers (agents)
                        a. "NOTE: (Reason to defer or ignore a finding goes here)" to document any rationale for deferring or ignoring a finding
                    </review_steps>
                    <process>
                    1. Findings and feedback are high level; plan out specific code changes needed finding-by-finding; ground in actual code
                    2. Clear explanation of the identified finding and explain tradeoffs, shortcomings, different angles in the reviewers findings if any (especially with grounding and broader context of the codebase)
                    3. Present the concrete and specific changes needed; user decides which to proceed with; **do not make changes without confirmation**
                    4. For verified findings, present multiple options for resolution as a pick list with a reasonable recommended default and explanation why
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
