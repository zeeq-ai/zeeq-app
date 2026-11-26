import { describe, expect, it, vi } from "vitest";
import type { FindingTriageResponse } from "@/api/generated";
import { resolveFindingTriageLink } from "./resolveFindingTriageLink";
const result = {
  triage: { occurrenceKey: "F2" },
  sourceReviewExpired: false,
  singleReviewUrl: "https://example.test/web/code-reviews/reviews/r?c=token",
} as FindingTriageResponse;
function deps() {
  return {
    activeOrgId: "org",
    organizationIds: ["org", "other"],
    switchOrganization: vi.fn().mockResolvedValue(undefined),
    load: vi.fn().mockResolvedValue(result),
    origin: "https://example.test",
    base: "/web/",
  };
}
describe("finding triage links", () => {
  it("preserves the token and adds the occurrence after stripping the app base", async () => {
    expect(await resolveFindingTriageLink("org", "t", deps())).toEqual({
      kind: "redirect",
      path: "/code-reviews/reviews/r?c=token&finding=F2",
    });
  });
  it("switches an authorized organization and leaves resolution for the reload", async () => {
    const d = deps();
    expect(await resolveFindingTriageLink("other", "t", d)).toEqual({
      kind: "switching",
    });
    expect(d.switchOrganization).toHaveBeenCalledWith("other");
    expect(d.load).not.toHaveBeenCalled();
  });
  it("rejects inaccessible organizations and exposes an expired snapshot", async () => {
    const d = deps();
    await expect(resolveFindingTriageLink("unknown", "t", d)).rejects.toThrow(
      "Organization is not available",
    );
    expect(d.switchOrganization).not.toHaveBeenCalled();
    expect(d.load).not.toHaveBeenCalled();
    d.load.mockResolvedValue({
      ...result,
      sourceReviewExpired: true,
      singleReviewUrl: null,
    });
    expect((await resolveFindingTriageLink("org", "t", d)).kind).toBe(
      "expired",
    );
  });
});
