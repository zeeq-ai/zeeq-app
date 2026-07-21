import type { OrgSummary } from "@/api/generated";

/**
 * Returns true only when the caller's membership and the organization activation
 * state are both usable for tenant-scoped app access.
 */
export function isActivatedOrganization(org: OrgSummary): boolean {
  return org.status === "Active" && org.activatedAtUtc !== null;
}
