/** Frontend view models for audit feeds. Snapshot content stays out of feed rows. */
export type AuditHistoryScope = "document" | "organization" | "system";

export type AuditHistoryRange = "90-days" | "all-time";

export type AuditHistoryEntryType = "added" | "edited" | "deleted";
export type AuditHistoryArea =
  "Content" | "ReviewConfiguration" | "Settings" | "Security";

export type AuditHistoryEntry = {
  id: string;
  /** Keep the exact API value for detail keys and ordering; Date would round microseconds to milliseconds. */
  occurredAtUtc: string;
  actorLabel: string;
  actorKind: string;
  actorId: string;
  actorChannelLabel: string;
  actionLabel: string;
  eventType: string;
  subjectType: string;
  subjectId: string;
  subjectLabel: string;
  subjectPath?: string | null;
  libraryId?: string | null;
  libraryLabel?: string | null;
  libraryLabelIsCurrent?: boolean;
  areaLabel: string;
  area: AuditHistoryArea | null;
  sourceLabel: string;
  type: AuditHistoryEntryType;
  organizationLabel?: string | null;
  actorIsSystemAdmin: boolean;
  partial: boolean;
  operationId?: string | null;
  /** Set by the API mapper only when the source reference verifies this operation. */
  verifiedSyncRunId?: string | null;
};

export type AuditHistoryFieldChange = {
  label: string;
  before: string | null;
  after: string | null;
  sensitive: boolean;
};

export type AuditHistoryDocumentSnapshot = {
  versionLabel: string;
  recordedAtUtc: string;
  fileName: string;
  content: string;
};

/** Browser rendering budget only; full snapshots remain available for copy/download above it. */
export const maxInlineAuditDiffCharacters = 200_000;
export const maxAuditHistoryGroupPageSize = 50;

export function getAuditHistoryGroupPage<T>(
  items: T[],
  page: number,
  requestedPageSize = maxAuditHistoryGroupPageSize,
) {
  const pageSize = Math.max(
    1,
    Math.min(Math.trunc(requestedPageSize) || 1, maxAuditHistoryGroupPageSize),
  );
  const pageCount = Math.max(1, Math.ceil(items.length / pageSize));
  const safePage = Math.min(Math.max(Math.trunc(page) || 0, 0), pageCount - 1);
  const offset = safePage * pageSize;
  return {
    items: items.slice(offset, offset + pageSize),
    page: safePage,
    pageCount,
    pageSize,
    start: items.length === 0 ? 0 : offset + 1,
    end: Math.min(offset + pageSize, items.length),
    total: items.length,
  };
}

/** Prevents large snapshots from mounting the in-browser syntax diff viewer. */
export function canCompareAuditSnapshotsInline(
  before: AuditHistoryDocumentSnapshot | null | undefined,
  after: AuditHistoryDocumentSnapshot | null | undefined,
): boolean {
  return (
    !!before &&
    !!after &&
    before.content.length + after.content.length <= maxInlineAuditDiffCharacters
  );
}

export type AuditHistoryDetail = {
  recordedAtUtc: string;
  changes: AuditHistoryFieldChange[];
  deletedLibrary?: {
    id: string;
    name: string;
    documentCount: number | null;
    repositoryMappingCount: number | null;
  };
  beforeSnapshot?: AuditHistoryDocumentSnapshot | null;
  afterSnapshot?: AuditHistoryDocumentSnapshot | null;
  sync?: {
    sourceReference: string | null;
    operationId: string;
  };
};

export type AuditHistoryDayGroup = {
  key: string;
  label: string;
  entries: AuditHistoryEntry[];
};

export type AuditHistoryOperationState = {
  hasMore: boolean;
  loading: boolean;
  error?: string | null;
};

export type AuditHistoryFeedItem =
  | {
      kind: "event";
      key: string;
      sortId: string;
      entry: AuditHistoryEntry;
      occurredAtUtc: string;
    }
  | {
      kind: "sync-run";
      key: string;
      sortId: string;
      operationId: string;
      entries: AuditHistoryEntry[];
      operationState: AuditHistoryOperationState;
      occurredAtUtc: string;
    }
  | {
      kind: "library-deletion";
      key: string;
      sortId: string;
      operationId: string;
      parent: AuditHistoryEntry;
      children: AuditHistoryEntry[];
      operationState: AuditHistoryOperationState;
      occurredAtUtc: string;
    };

export type AuditHistoryFeedDayGroup = {
  key: string;
  label: string;
  items: AuditHistoryFeedItem[];
};

/** Translates the current backend coverage key without claiming uncaptured classes. */
export function formatAuditCoverage(coverage: string): string {
  if (
    coverage.startsWith(
      "partial: direct document changes, library lifecycle and repository-library mappings",
    )
  ) {
    return "History records direct document changes, library creation, updates and deletions, repository-library mapping changes, public-source registration in system history, organization administration, membership changes, and activation keys. Other configuration and external actions are not recorded yet. Public-source registration does not include historical synced document content.";
  }
  return coverage;
}

/** Groups entries by UTC date, which matches the audit partition boundary. */
export function groupAuditEntriesByUtcDay(
  entries: AuditHistoryEntry[],
  locale?: string,
): AuditHistoryDayGroup[] {
  const groups = new Map<string, AuditHistoryEntry[]>();

  for (const entry of entries) {
    const day = entry.occurredAtUtc.slice(0, 10);
    const dayEntries = groups.get(day) ?? [];
    dayEntries.push(entry);
    groups.set(day, dayEntries);
  }

  return [...groups.entries()].map(([key, dayEntries]) => ({
    key,
    label: formatUtcDay(key, locale),
    entries: dayEntries,
  }));
}

/** Groups only loaded sync facts with a source reference that verifies the operation id. */
// A request operation can contain fallback facts unrelated to a durable sync run. Even verified
// children are only the loaded subset; their count is not a completed run manifest or final status.
export function groupAuditFeedEntries(
  entries: AuditHistoryEntry[],
  operationStates: Record<string, AuditHistoryOperationState>,
): AuditHistoryFeedItem[] {
  const items: AuditHistoryFeedItem[] = [];
  const syncRuns = new Map<
    string,
    Extract<AuditHistoryFeedItem, { kind: "sync-run" }>
  >();

  const deletionParents = new Map<string, AuditHistoryEntry>();
  for (const entry of entries) {
    if (
      entry.eventType === "library.deleted" &&
      entry.subjectType.toLowerCase() === "library" &&
      entry.subjectId &&
      entry.libraryId === entry.subjectId &&
      entry.operationId
    ) {
      deletionParents.set(
        getLibraryOperationKey(entry.operationId, entry.subjectId),
        entry,
      );
    }
  }

  const linkedChildren = new Map<string, AuditHistoryEntry[]>();
  for (const entry of entries) {
    if (!isLibraryDeletionChild(entry)) continue;
    const parentKey = getLibraryOperationKey(
      entry.operationId!,
      entry.libraryId!,
    );
    if (!deletionParents.has(parentKey)) continue;
    const children = linkedChildren.get(parentKey) ?? [];
    children.push(entry);
    linkedChildren.set(parentKey, children);
  }

  const emittedDeletionGroups = new Set<string>();

  for (const entry of entries) {
    if (
      entry.eventType === "library.deleted" &&
      entry.subjectType.toLowerCase() === "library" &&
      entry.subjectId &&
      entry.libraryId === entry.subjectId &&
      entry.operationId
    ) {
      const parentKey = getLibraryOperationKey(
        entry.operationId,
        entry.subjectId,
      );
      if (emittedDeletionGroups.has(parentKey)) continue;
      emittedDeletionGroups.add(parentKey);
      const children = linkedChildren.get(parentKey) ?? [];
      items.push({
        kind: "library-deletion",
        key: `library-deletion-${parentKey}`,
        sortId: entry.id,
        operationId: entry.operationId,
        parent: entry,
        children,
        operationState: operationStates[entry.operationId] ?? {
          hasMore: true,
          loading: false,
          error: null,
        },
        occurredAtUtc: entry.occurredAtUtc,
      });
      continue;
    }

    if (isLibraryDeletionChild(entry)) {
      const parentKey = getLibraryOperationKey(
        entry.operationId!,
        entry.libraryId!,
      );
      if (deletionParents.has(parentKey)) continue;
    }

    const verifiedOperationId = getVerifiedSyncOperationId(entry);

    if (verifiedOperationId) {
      const existing = syncRuns.get(verifiedOperationId);

      if (existing) {
        existing.entries.push(entry);
      } else {
        const syncRun: Extract<AuditHistoryFeedItem, { kind: "sync-run" }> = {
          kind: "sync-run",
          key: `sync-${verifiedOperationId}`,
          sortId: entry.id,
          operationId: entry.operationId!,
          entries: [entry],
          occurredAtUtc: entry.occurredAtUtc,
          operationState: operationStates[entry.operationId!] ?? {
            hasMore: true,
            loading: false,
            error: null,
          },
        };
        syncRuns.set(verifiedOperationId, syncRun);
        items.push(syncRun);
      }
    } else {
      items.push({
        kind: "event",
        key: entry.id,
        sortId: entry.id,
        entry,
        occurredAtUtc: entry.occurredAtUtc,
      });
    }
  }

  return items.sort((left, right) =>
    compareAuditEntriesDescending(
      { occurredAtUtc: left.occurredAtUtc, id: left.sortId },
      { occurredAtUtc: right.occurredAtUtc, id: right.sortId },
    ),
  );
}

export function groupAuditFeedEntriesByUtcDay(
  entries: AuditHistoryEntry[],
  operationStates: Record<string, AuditHistoryOperationState>,
  locale?: string,
): AuditHistoryFeedDayGroup[] {
  const groups = new Map<string, AuditHistoryFeedItem[]>();
  for (const item of groupAuditFeedEntries(entries, operationStates)) {
    const day = item.occurredAtUtc.slice(0, 10);
    const dayItems = groups.get(day) ?? [];
    dayItems.push(item);
    groups.set(day, dayItems);
  }

  return [...groups.entries()].map(([key, items]) => ({
    key,
    label: formatUtcDay(key, locale),
    items,
  }));
}

function isLibraryDeletionChild(entry: AuditHistoryEntry): boolean {
  return (
    !!entry.operationId &&
    !!entry.libraryId &&
    ((entry.eventType === "document.deleted" &&
      entry.subjectType.toLowerCase() === "document") ||
      (entry.eventType === "repository.library_mapping_changed" &&
        entry.subjectType.toLowerCase() === "repository"))
  );
}

function getLibraryOperationKey(operationId: string, libraryId: string) {
  return `${operationId.toLowerCase()}\u0000${libraryId.toLowerCase()}`;
}

function compareAuditEntriesDescending(
  left: Pick<AuditHistoryEntry, "occurredAtUtc" | "id">,
  right: Pick<AuditHistoryEntry, "occurredAtUtc" | "id">,
): number {
  const timestampOrder = compareAuditTimestampsDescending(
    left.occurredAtUtc,
    right.occurredAtUtc,
  );
  if (timestampOrder !== 0) return timestampOrder;
  const leftId = left.id.toLowerCase();
  const rightId = right.id.toLowerCase();
  return leftId === rightId ? 0 : leftId > rightId ? -1 : 1;
}

function compareAuditTimestampsDescending(left: string, right: string): number {
  const leftWholeSecond = Date.parse(
    left.replace(/\.\d+(?=(?:Z|[+-]\d{2}:?\d{2})?$)/i, ""),
  );
  const rightWholeSecond = Date.parse(
    right.replace(/\.\d+(?=(?:Z|[+-]\d{2}:?\d{2})?$)/i, ""),
  );
  if (
    Number.isFinite(leftWholeSecond) &&
    Number.isFinite(rightWholeSecond) &&
    leftWholeSecond !== rightWholeSecond
  ) {
    return rightWholeSecond - leftWholeSecond;
  }
  if (leftWholeSecond !== rightWholeSecond) {
    return left === right ? 0 : left > right ? -1 : 1;
  }

  const leftFraction = timestampFraction(left);
  const rightFraction = timestampFraction(right);
  const fractionLength = Math.max(leftFraction.length, rightFraction.length);
  const normalizedLeft = leftFraction.padEnd(fractionLength, "0");
  const normalizedRight = rightFraction.padEnd(fractionLength, "0");
  return normalizedLeft === normalizedRight
    ? 0
    : normalizedLeft > normalizedRight
      ? -1
      : 1;
}

function timestampFraction(value: string): string {
  return value.match(/\.(\d+)(?=(?:Z|[+-]\d{2}:?\d{2})?$)/i)?.[1] ?? "";
}

function getVerifiedSyncOperationId(entry: AuditHistoryEntry): string | null {
  if (
    entry.eventType !== "document.synced" ||
    !entry.operationId ||
    !entry.verifiedSyncRunId
  ) {
    return null;
  }

  const operationHex = normalizeGuid(entry.operationId);
  const runHex = normalizeGuid(entry.verifiedSyncRunId);
  return operationHex && operationHex === runHex ? operationHex : null;
}

function normalizeGuid(value: string): string | null {
  const normalized = value.replaceAll("-", "").toLowerCase();
  return /^[0-9a-f]{32}$/.test(normalized) ? normalized : null;
}

/** Formats a timestamp for the user's locale while preserving its UTC meaning. */
export function formatAuditTime(value: string, locale?: string): string {
  const date = new Date(value);

  if (Number.isNaN(date.getTime())) {
    return "Unknown time";
  }

  return new Intl.DateTimeFormat(locale, {
    hour: "numeric",
    minute: "2-digit",
  }).format(date);
}

/** Formats a local date and time for a standalone document timeline row. */
export function formatAuditDateTime(value: string, locale?: string): string {
  const date = new Date(value);

  if (Number.isNaN(date.getTime())) {
    return "Unknown time";
  }

  return new Intl.DateTimeFormat(locale, {
    dateStyle: "medium",
    timeStyle: "short",
  }).format(date);
}

export function formatAuditTimelineDateTime(
  value: string,
  locale?: string,
): string {
  const date = new Date(value);

  if (Number.isNaN(date.getTime())) {
    return "Unknown time";
  }

  const options: Intl.DateTimeFormatOptions = {
    month: "short",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit",
  };
  if (date.getFullYear() !== new Date().getFullYear()) {
    options.year = "numeric";
  }

  return new Intl.DateTimeFormat(locale, options).format(date);
}

function formatUtcDay(day: string, locale?: string): string {
  const date = new Date(`${day}T00:00:00.000Z`);

  if (Number.isNaN(date.getTime())) {
    return day;
  }

  return new Intl.DateTimeFormat(locale, {
    timeZone: "UTC",
    weekday: "long",
    month: "long",
    day: "numeric",
    year: "numeric",
  }).format(date);
}
