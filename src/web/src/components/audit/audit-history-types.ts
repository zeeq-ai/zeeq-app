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
  subjectLabel: string;
  subjectPath?: string | null;
  libraryId?: string | null;
  libraryLabel?: string | null;
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
  | { kind: "event"; key: string; entry: AuditHistoryEntry }
  | {
      kind: "sync-run";
      key: string;
      operationId: string;
      entries: AuditHistoryEntry[];
      operationState: AuditHistoryOperationState;
    };

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

  for (const entry of entries) {
    const verifiedOperationId = getVerifiedSyncOperationId(entry);

    if (verifiedOperationId) {
      const existing = syncRuns.get(verifiedOperationId);

      if (existing) {
        existing.entries.push(entry);
      } else {
        const syncRun: Extract<AuditHistoryFeedItem, { kind: "sync-run" }> = {
          kind: "sync-run",
          key: `sync-${verifiedOperationId}`,
          operationId: entry.operationId!,
          entries: [entry],
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
      items.push({ kind: "event", key: entry.id, entry });
    }
  }

  return items;
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
