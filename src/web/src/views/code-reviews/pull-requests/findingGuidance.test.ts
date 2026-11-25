import { describe, expect, it } from "vitest";
import type {
  CodeReviewFindingDto,
  CodeReviewFindingTriageDto,
} from "@/api/generated";
import {
  formatFindingGuidanceXml,
  formatReviewerSectionGuidance,
} from "./findingGuidance";
const finding: CodeReviewFindingDto = {
  level: "Major",
  file: "a&b",
  line: 7,
  side: "RIGHT",
  summary: 'quoted "text"',
  body: "text ]]> continued",
  occurrenceKey: "F1",
  findingRef: "12345.F1",
  semanticId: "test-finding-one",
  triage: null,
};
describe("finding XML guidance", () => {
  it("preserves identity and escapes attributes and CDATA", () => {
    const xml = formatFindingGuidanceXml(finding, 0);
    expect(xml).toContain('ref="12345.F1" semanticId="test-finding-one"');
    expect(xml).toContain('summary="quoted &quot;text&quot;" file="a&amp;b"');
    expect(xml).toContain("]]]]><![CDATA[>");
  });
  it("excludes confirmed findings but keeps open and proposed findings", () => {
    const confirmed = {
      ...finding,
      summary: "excluded",
      triage: { approvalState: "Confirmed" } as CodeReviewFindingTriageDto,
    };
    const proposed = {
      ...finding,
      summary: "pending",
      triage: { approvalState: "Proposed" } as CodeReviewFindingTriageDto,
    };
    const section = {
      reviewer: {
        facet: "Security",
        agent: "Reviewer",
        summary: "",
        details: "",
        findings: [finding, confirmed, proposed],
      },
      findings: [finding, confirmed, proposed],
    };
    const xml = formatReviewerSectionGuidance(section, {
      id: "review",
      reviewGroupId: "group",
    });
    expect(xml).toContain('reviewId="review"');
    expect(xml).toContain('reviewGroupId="group"');
    expect(xml).toContain("triage_review_findings");
    expect(xml).toContain('summary="pending"');
    expect(xml).not.toContain('summary="excluded"');
  });
});
