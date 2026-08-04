import type { MemberResponse } from "@/api/generated";

/** DTO numeric fields are typed `number | string` (large-number-safe JSON encoding). */
type ApiNumber = number | string | null | undefined;

/** Coerces an API numeric field to a plain `number`, treating null/undefined as 0. */
export function toApiNumber(value: ApiNumber): number {
  if (value === null || value === undefined) {
    return 0;
  }

  return typeof value === "number" ? value : Number(value) || 0;
}

/**
 * Resolves a friendly display label for a conversation owner by matching the
 * raw `createdById`/`ownerEmail` telemetry fields against the organization's
 * already-loaded member list (same resolution `AgentUsageTab.vue`'s
 * `memberUsageItems` computed does for Home dashboard usage rows) — the
 * backend intentionally returns raw identity fields rather than joining a
 * display name server-side.
 */
export function resolveOwnerLabel(
  members: MemberResponse[],
  ownerEmail: string | null | undefined,
  createdById: string | null | undefined,
): string {
  const byId = createdById
    ? members.find((member) => member.userId === createdById)
    : undefined;
  if (byId) {
    return byId.displayName || byId.email || byId.userId;
  }

  const normalizedEmail = ownerEmail?.trim().toLowerCase();
  const byEmail = normalizedEmail
    ? members.find(
        (member) => member.email?.trim().toLowerCase() === normalizedEmail,
      )
    : undefined;
  if (byEmail) {
    return byEmail.displayName || byEmail.email || byEmail.userId;
  }

  return ownerEmail ?? "Unknown";
}

/**
 * True for Claude Code's background-task lifecycle pings (`<task-notification>...`),
 * which land in the transcript as an ordinary prompt event — there's no structured
 * flag anywhere in the ingest pipeline that distinguishes them from a real user
 * message (`IsHousekeeping`/`QuerySource` don't catch them; they arrive on the same
 * main thread as genuine prompts), so this is a content-based filter of last resort.
 */
export function isTaskNotificationPrompt(
  promptText: string | null | undefined,
): boolean {
  return promptText?.trimStart().startsWith("<task-notification>") === true;
}

/** Fuller date/time for the detail sidebar and timeline items. */
export function formatFullDateTime(value: Date | string): string {
  return new Intl.DateTimeFormat(undefined, {
    dateStyle: "medium",
    timeStyle: "short",
  }).format(new Date(value));
}

/** Locale-formatted whole-number token count, e.g. `4,267`. */
export function formatTokenCount(value: ApiNumber): string {
  return new Intl.NumberFormat().format(Math.round(toApiNumber(value)));
}

/**
 * Compact per-turn token summary for a timeline row, e.g. `1,234 in / 56 out tokens`.
 * Renders "No completions yet" when neither side has a value — the turn's still in flight.
 */
export function formatTurnTokens(
  inputTokens: ApiNumber,
  outputTokens: ApiNumber,
): string {
  if (
    (inputTokens === null || inputTokens === undefined) &&
    (outputTokens === null || outputTokens === undefined)
  ) {
    return "No completions yet";
  }

  return `${formatTokenCount(inputTokens)} in / ${formatTokenCount(outputTokens)} out tokens`;
}

/** Locale-formatted USD, e.g. `$0.02`; renders "—" for null/undefined (cost unknown, not $0). */
export function formatUsd(value: ApiNumber): string {
  if (value === null || value === undefined) {
    return "—";
  }

  return toApiNumber(value).toLocaleString(undefined, {
    style: "currency",
    currency: "USD",
    maximumFractionDigits: 2,
  });
}

/** Locale-formatted percentage, e.g. `72.4%`; renders "—" for null/undefined. */
export function formatPercent(value: ApiNumber): string {
  if (value === null || value === undefined) {
    return "—";
  }

  return toApiNumber(value).toLocaleString(undefined, {
    style: "percent",
    maximumFractionDigits: 1,
  });
}

/**
 * Compact elapsed-time string between two timestamps, e.g. `2h 17m`, `45m 12s`, `23s`.
 * Extends the same h/m/s convention `agent-test-view-models.ts`'s `formatRunTime` uses for
 * agent test runs, adding hours since a session can run far longer than one test run.
 */
export function formatDuration(
  startedAtUtc: Date | string | null | undefined,
  endAtUtc: Date | string | null | undefined,
): string {
  if (!startedAtUtc || !endAtUtc) {
    return "—";
  }

  const elapsedMs = new Date(endAtUtc).getTime() - new Date(startedAtUtc).getTime();
  if (!Number.isFinite(elapsedMs) || elapsedMs <= 0) {
    return "N/A";
  }

  const elapsedSeconds = Math.round(elapsedMs / 1000);
  const hours = Math.floor(elapsedSeconds / 3600);
  const minutes = Math.floor((elapsedSeconds % 3600) / 60);
  const seconds = elapsedSeconds % 60;

  if (hours > 0) {
    return minutes === 0 ? `${hours}h` : `${hours}h ${minutes}m`;
  }
  if (minutes > 0) {
    return seconds === 0 ? `${minutes}m` : `${minutes}m ${seconds}s`;
  }

  return `${seconds}s`;
}
