/**
 * Shared organization-admin view rules used by the table and slideover tabs.
 *
 * These are plain functions instead of a composable because they do not own
 * reactive state; they centralize domain and formatting rules that must stay
 * consistent across sibling components.
 */

/** Page sizes accepted by the system organization list and member controls. */
export const systemOrganizationPageSizeOptions = [10, 25, 50, 100] as const;

/**
 * Mirrors backend active semantics: activated rows are active unless disabled.
 */
export function isSystemOrganizationActive(organization: {
  activatedAtUtc: Date | string | null;
  disabledAtUtc: Date | string | null;
}) {
  // NOTE: This intentionally mirrors backend OrganizationActivationState.IsActive:
  // an organization is active only after activation has been recorded and no
  // disabled timestamp is present.
  return (
    organization.activatedAtUtc !== null && organization.disabledAtUtc === null
  );
}

/**
 * Clamps one-based pagination to the last page visible for a backend total.
 */
export function clampSystemOrganizationPage(
  page: number,
  pageSize: number,
  totalCount: number,
) {
  const lastPage = Math.max(1, Math.ceil(totalCount / pageSize));

  return Math.min(Math.max(1, page), lastPage);
}

/**
 * Parses page-size control values and rejects sizes outside the supported set.
 */
export function toSystemOrganizationPageSize(
  value: string | number | null | undefined,
) {
  const pageSize = typeof value === "number" ? value : Number(value);

  return systemOrganizationPageSizeOptions.some((option) => option === pageSize)
    ? pageSize
    : null;
}

/**
 * Formats generated Date/string response values for compact admin rows.
 */
export function formatSystemOrganizationDate(value: Date | string): string {
  return new Date(value).toLocaleDateString(undefined, {
    year: "numeric",
    month: "short",
    day: "numeric",
  });
}
