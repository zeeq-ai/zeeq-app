import type { FindingTriageResponse } from "@/api/generated";

type Dependencies = {
  activeOrgId: string | null | undefined;
  organizationIds: string[];
  switchOrganization: (id: string) => Promise<void>;
  load: (id: string) => Promise<FindingTriageResponse>;
  origin: string;
  base: string;
};

/** Organization switching reloads the current URL; the next mount resolves the triage. */
export async function resolveFindingTriageLink(
  orgId: string,
  triageId: string,
  deps: Dependencies,
) {
  if (!deps.organizationIds.includes(orgId))
    throw new Error("Organization is not available.");
  if (deps.activeOrgId !== orgId) {
    await deps.switchOrganization(orgId);
    return { kind: "switching" as const };
  }
  const result = await deps.load(triageId);
  if (!result.singleReviewUrl || result.sourceReviewExpired)
    return { kind: "expired" as const, result };
  const url = new URL(result.singleReviewUrl, deps.origin);
  url.searchParams.set("finding", result.triage.occurrenceKey);
  const base = deps.base.replace(/\/$/, "");
  const path =
    base && url.pathname.startsWith(`${base}/`)
      ? url.pathname.slice(base.length)
      : url.pathname;
  return { kind: "redirect" as const, path: path + url.search };
}
