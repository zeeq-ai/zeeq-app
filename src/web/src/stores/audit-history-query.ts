import type { ListAuditHistoryQueryParams } from "@/api/generated";
import type {
  AuditHistoryArea,
  AuditHistoryEntryType,
  AuditHistoryRange,
  AuditHistoryScope,
} from "@/components/audit/audit-history-types";

export type AuditHistoryContextIdentity = {
  scope: AuditHistoryScope;
  organizationId?: string | null;
  subjectId?: string | null;
  subjectType?: string | null;
  libraryId?: string | null;
};

type AuditHistoryQueryContext = AuditHistoryContextIdentity;

/** Compares the request identity and ignores display-only library labels. */
export function hasAuditHistoryContextChanged(
  current: AuditHistoryContextIdentity | null | undefined,
  next: AuditHistoryContextIdentity,
): boolean {
  return (
    current?.scope !== next.scope ||
    current?.organizationId !== next.organizationId ||
    current?.subjectId !== next.subjectId ||
    current?.subjectType !== next.subjectType ||
    current?.libraryId !== next.libraryId
  );
}

/** Avoid leaving a visible feed attached to the scope it was opened for. */
export function shouldCloseAuditHistoryForContextChange(
  isOpen: boolean,
  current: AuditHistoryContextIdentity | null | undefined,
  next: AuditHistoryContextIdentity,
): boolean {
  return isOpen && hasAuditHistoryContextChanged(current, next);
}

export type AuditHistoryFilters = {
  type: AuditHistoryEntryType | "all";
  area: AuditHistoryArea | "all";
  actorId: string | null;
};

/** Builds Kubb query parameters while retaining the active filters for each cursor page. */
export function buildAuditHistoryQuery(
  context: AuditHistoryQueryContext,
  range: AuditHistoryRange,
  filters: AuditHistoryFilters,
  options: { cursor?: string; operationId?: string } = {},
): ListAuditHistoryQueryParams {
  return {
    Cursor: options.cursor,
    Limit: 50,
    AllTime: range === "all-time",
    ChangeType: toApiChangeType(filters.type),
    Area: filters.area === "all" ? undefined : filters.area,
    ActorId: filters.actorId ?? undefined,
    SubjectType: context.subjectType ?? undefined,
    SubjectId: context.subjectId ?? undefined,
    LibraryId: context.libraryId ?? undefined,
    OperationId: options.operationId,
  };
}

export function isSystemAuditContext(
  context: AuditHistoryQueryContext,
): boolean {
  return context.scope === "system";
}

function toApiChangeType(type: AuditHistoryEntryType | "all") {
  switch (type) {
    case "added":
      return "Added" as const;
    case "deleted":
      return "Deleted" as const;
    case "edited":
      return "Edited" as const;
    default:
      return undefined;
  }
}
