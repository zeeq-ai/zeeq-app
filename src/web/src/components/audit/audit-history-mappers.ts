import {
  formatAuditAction,
  formatAuditActor,
  formatAuditArea,
  formatAuditSourceReference,
  isAuditPartialOutcome,
} from "./audit-history-formatters";
import type {
  AuditHistoryDetail,
  AuditHistoryArea,
  AuditHistoryDocumentSnapshot,
  AuditHistoryEntry,
  AuditHistoryEntryType,
  AuditHistoryFieldChange,
} from "./audit-history-types";

/** Structural API shape. Kubb owns the transport types; this mapper owns display policy. */
export type AuditEventSummaryDto = {
  id: string;
  occurredAtUtc: string | Date;
  scope: "Organization" | "System" | string;
  organizationLabel?: string | null;
  eventType: string;
  changeType: "Added" | "Edited" | "Deleted" | string;
  area: "Content" | "ReviewConfiguration" | "Settings" | "Security" | string;
  subjectType: string;
  subjectId: string;
  subjectLabel: string;
  libraryId?: string | null;
  actor: {
    kind: string;
    id: string;
    displayName: string;
    channel: string;
    responsibleUserId?: string | null;
    credentialId?: string | null;
    credentialName?: string | null;
    initiatedByUserId?: string | null;
  };
  operationId: string;
  changedFields?: string[] | null;
  sourceReference?: string | null;
  outcome?: string | null;
  hasSnapshots?: boolean;
};

export type AuditSnapshotDto = {
  path?: string | null;
  title?: string | null;
  content?: string | null;
  fields?: Record<string, string | null> | null;
};

export type AuditEventDetailDto = {
  event: AuditEventSummaryDto;
  before?: AuditSnapshotDto | null;
  after?: AuditSnapshotDto | null;
};

const safeFieldLabels: Record<string, string> = {
  path: "Path",
  title: "Title",
  name: "Name",
  displayname: "Display name",
  role: "Role",
  tier: "Organization tier",
  status: "Status",
  adminstate: "Admin state",
  active: "Organization status",
  isactive: "Active",
  enabled: "Enabled",
  defaultrole: "Default role",
  excludedfromcodereviews: "Code review inclusion",
  excludedfromreview: "Code review inclusion",
  reviewexcluded: "Code review inclusion",
  scopedskill: "Organization skill",
  skillscope: "Organization skill",
  expiresatutc: "Expiration date",
  slug: "Slug",
  charactercount: "Character count",
  externalid: "External ID",
};

/** Maps a metadata row to safe, readable list content. Secret IDs and hashes are ignored. */
export function mapAuditEventSummary(
  event: AuditEventSummaryDto,
): AuditHistoryEntry {
  const type = toEntryType(event.changeType);
  const actor = formatAuditActor(event.actor);
  return {
    id: event.id,
    occurredAtUtc: toIsoString(event.occurredAtUtc),
    actorLabel: actor.label,
    actorKind: event.actor.kind,
    actorId: event.actor.id,
    actorChannelLabel: actor.channelLabel,
    actionLabel: formatAuditAction(event.eventType, type),
    eventType: event.eventType,
    subjectLabel: event.subjectLabel || "Unknown item",
    areaLabel: formatAuditArea(event.area),
    area: isAuditArea(event.area) ? event.area : null,
    sourceLabel:
      event.eventType === "document.synced"
        ? "Sync"
        : formatAuditSourceReference(
            event.sourceReference,
            event.subjectType,
            event.scope === "System" ? "System" : "Organization",
          ),
    type,
    organizationLabel: event.organizationLabel,
    actorIsSystemAdmin: actor.isSystemAdmin,
    partial: isAuditPartialOutcome(event.outcome),
    operationId: event.operationId,
    verifiedSyncRunId: getVerifiedSyncRunId(
      event.sourceReference,
      event.operationId,
      event.eventType,
    ),
  };
}

function getVerifiedSyncRunId(
  sourceReference: string | null | undefined,
  operationId: string,
  eventType: string,
): string | null {
  if (eventType !== "document.synced" || !sourceReference) {
    return null;
  }

  const match = /^sync-run:run_([0-9a-f]{32})$/i.exec(sourceReference);
  const operationHex = operationId.replaceAll("-", "").toLowerCase();
  if (
    !match ||
    !/^[0-9a-f]{32}$/.test(operationHex) ||
    match[1]?.toLowerCase() !== operationHex
  ) {
    return null;
  }

  return match[1].toLowerCase();
}

/** Converts lazy snapshots into allowlisted scalar details and local document versions. */
export function mapAuditEventDetail(
  detail: AuditEventDetailDto,
): AuditHistoryDetail {
  const recordedAtUtc = toIsoString(detail.event.occurredAtUtc);
  const isLocalDocument =
    isDocument(detail.event.subjectType) &&
    detail.event.eventType.startsWith("document.") &&
    detail.event.eventType !== "document.synced";

  return {
    recordedAtUtc,
    changes: mapFieldChanges(
      detail.event.changedFields ?? [],
      snapshotValues(detail.before),
      snapshotValues(detail.after),
    ),
    beforeSnapshot: isLocalDocument
      ? mapDocumentSnapshot(detail.before, "Before this change", recordedAtUtc)
      : null,
    afterSnapshot: isLocalDocument
      ? mapDocumentSnapshot(detail.after, "After this change", recordedAtUtc)
      : null,
    ...(detail.event.eventType === "document.synced"
      ? {
          sync: {
            sourceReference: detail.event.sourceReference ?? null,
            operationId: detail.event.operationId,
          },
        }
      : {}),
  };
}

function mapFieldChanges(
  changedFields: string[],
  before: Record<string, string | null> | null | undefined,
  after: Record<string, string | null> | null | undefined,
): AuditHistoryFieldChange[] {
  const seen = new Set<string>();
  const beforeValues = normalizeFieldValues(before);
  const afterValues = normalizeFieldValues(after);
  const changes: AuditHistoryFieldChange[] = [];

  for (const field of changedFields) {
    const normalized = normalizeFieldName(field);
    const label = safeFieldLabels[normalized];
    const sensitiveLabel = getSensitiveFieldLabel(normalized);

    if ((!label && !sensitiveLabel) || seen.has(normalized)) {
      continue;
    }

    seen.add(normalized);
    if (sensitiveLabel) {
      changes.push({
        label: sensitiveLabel,
        before: null,
        after: null,
        sensitive: true,
      });
      continue;
    }

    const beforeExists = Object.hasOwn(beforeValues, normalized);
    const afterExists = Object.hasOwn(afterValues, normalized);
    if (!beforeExists && !afterExists) continue;

    changes.push({
      label,
      before: formatFieldValue(normalized, beforeValues[normalized]),
      after: formatFieldValue(normalized, afterValues[normalized]),
      sensitive: false,
    });
  }

  return changes;
}

function getSensitiveFieldLabel(field: string): string | null {
  if (field.includes("password")) return "Password";
  if (field.includes("token")) return "Token";
  if (field.includes("apikey") || field.includes("keyvalue")) return "API key";
  if (field.includes("secret") || field.includes("privatekey")) {
    return "Credential secret";
  }
  return null;
}

function mapDocumentSnapshot(
  snapshot: AuditSnapshotDto | null | undefined,
  versionLabel: string,
  recordedAtUtc: string,
): AuditHistoryDocumentSnapshot | null {
  if (typeof snapshot?.content !== "string") {
    return null;
  }

  const path = snapshot.path?.trim() || snapshot.title?.trim() || "document.md";

  return {
    versionLabel,
    recordedAtUtc,
    fileName: safeFileName(path),
    content: snapshot.content,
  };
}

function normalizeFieldValues(
  fields: Record<string, string | null> | null | undefined,
): Record<string, string | null> {
  return Object.fromEntries(
    Object.entries(fields ?? {}).map(([key, value]) => [
      normalizeFieldName(key),
      value,
    ]),
  );
}

function snapshotValues(
  snapshot: AuditSnapshotDto | null | undefined,
): Record<string, string | null> {
  return {
    ...normalizeFieldValues(snapshot?.fields),
    ...(snapshot?.path !== undefined ? { path: snapshot.path } : {}),
    ...(snapshot?.title !== undefined ? { title: snapshot.title } : {}),
  };
}

function formatFieldValue(field: string, value: string | null | undefined) {
  if (value == null) return null;

  if (
    field === "excludedfromcodereviews" ||
    field === "excludedfromreview" ||
    field === "reviewexcluded"
  ) {
    return value === "true"
      ? "Excluded"
      : value === "false"
        ? "Included"
        : value;
  }
  if (field === "active" || field === "isactive" || field === "enabled") {
    return value === "true" ? "Active" : value === "false" ? "Inactive" : value;
  }
  if (field === "expiresatutc") {
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? value : date.toLocaleDateString();
  }

  return value;
}

function normalizeFieldName(field: string): string {
  return field
    .replace(/[-_]/g, "")
    .replace(/[A-Z]/g, (letter) => letter.toLowerCase());
}

function safeFileName(path: string): string {
  const basename = path.replace(/\\/g, "/").split("/").pop() || "document.md";
  const sanitized = basename.replace(/[\u0000-\u001f<>:"/\\|?*]/g, "_").trim();
  return sanitized || "document.md";
}

function isDocument(subjectType: string): boolean {
  return subjectType.toLowerCase() === "document";
}

function isAuditArea(area: string): area is AuditHistoryArea {
  return ["Content", "ReviewConfiguration", "Settings", "Security"].includes(
    area,
  );
}

function toIsoString(value: string | Date): string {
  if (value instanceof Date) return value.toISOString();
  return value;
}

function toEntryType(changeType: string): AuditHistoryEntryType {
  switch (changeType) {
    case "Added":
      return "added";
    case "Deleted":
      return "deleted";
    default:
      return "edited";
  }
}
