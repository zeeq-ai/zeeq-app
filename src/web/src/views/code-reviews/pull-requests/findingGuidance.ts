import type {
  CodeReviewFindingDto,
  CodeReviewRecordDto,
  CodeReviewReviewerFindingsDto,
} from "@/api/generated";
type ReviewerFindingSection = {
  reviewer: CodeReviewReviewerFindingsDto;
  findings: CodeReviewFindingDto[];
};

/**
 * Recreates the reviewer guidance as XML from the parsed DTO fields.
 */
export function formatReviewerSectionGuidance(
  section: ReviewerFindingSection,
  review: Pick<CodeReviewRecordDto, "id" | "reviewGroupId">,
): string {
  const findingXml = section.findings
    .filter((finding) => finding.triage?.approvalState !== "Confirmed")
    .map((finding) => formatFindingGuidanceXml(finding, 6))
    .join("\n");

  return `<code_review_finding>
  <!--
  <instruction_for_agents>
  The following XML is the raw output from expert code reviewers analyzing the PR.
  - Review each finding in the feedback from the expert reviewers
  - Evaluate the change in the broader context of the codebase; the reviewer only saw the PR contents and not the broader codebase; determine the veracity of each finding
  - If a behavior is expected, suggest leaving a comment in the code to explain the rationale to future travelers (agents)
  - The change proposals are high level; plan out specific code changes needed to implement the feedback, finding by finding
  - ALWAYS get confirmation and acceptance of the proposed fix for each finding before writing code
  - ALWAYS ensure there is enough clarity to make the best fix if there is ambiguity or insufficient feedback to confidently implement the change
  - Call out any tradeoffs or shortcomings in the proposed changes if any (especially in the broader context of the codebase)
  - Present the concrete changes needed and let the user decide which to proceed with; do not make changes without confirmation
  - Use a checklist/todo list to keep track of work against this set of findings; we do not want to wing it here
  - Review Zeeq documentation for guidance on best practices
  - Confirm decisions with the user and call triage_review_finding with reviewId and the finding ref and reason for false positives or deferrals.
  </instruction_for_agents>
  -->
  <review
    reviewId="${escapeXmlAttribute(review.id)}"
    reviewGroupId="${escapeXmlAttribute(review.reviewGroupId ?? "")}"
    facet="${escapeXmlAttribute(section.reviewer.facet)}"
    agent="${escapeXmlAttribute(section.reviewer.agent)}">
    <findings>
${findingXml}
    </findings>
  </review>
</code_review_finding>`;
}

/**
 * Formats one finding DTO as the canonical finding XML shape.
 */
export function formatFindingGuidanceXml(
  finding: CodeReviewFindingDto,
  indentSize: number,
): string {
  const indent = " ".repeat(indentSize);
  const attributes = [
    `ref="${escapeXmlAttribute(finding.findingRef)}"`,
    `semanticId="${escapeXmlAttribute(finding.semanticId)}"`,
    `level="${escapeXmlAttribute(finding.level)}"`,
    `summary="${escapeXmlAttribute(finding.summary)}"`,
    `file="${escapeXmlAttribute(finding.file)}"`,
    finding.line ? `line="${escapeXmlAttribute(String(finding.line))}"` : null,
    finding.side ? `side="${escapeXmlAttribute(finding.side)}"` : null,
  ]
    .filter((attribute) => attribute !== null)
    .join(" ");

  return `${indent}<finding ${attributes}><![CDATA[${escapeCdata(finding.body)}]]></finding>`;
}

/**
 * Escapes XML attribute values reconstructed from DTO strings.
 */
function escapeXmlAttribute(value: string): string {
  return value
    .replaceAll("&", "&amp;")
    .replaceAll('"', "&quot;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;");
}

/**
 * Prevents copied finding bodies from terminating the CDATA section early.
 */
function escapeCdata(value: string): string {
  return value.replaceAll("]]>", "]]]]><![CDATA[>");
}
