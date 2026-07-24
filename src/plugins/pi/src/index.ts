import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import { execFileSync } from "node:child_process";
import { createHash, randomUUID } from "node:crypto";

// Pi lifecycle telemetry exporter for Zeeq's agent telemetry import API.
//
// Documentation for future maintainers:
//   src/plugins/pi/README.md
//
// Relevant Pi references:
//   Extension auto-discovery:
//     https://pi.dev/docs/latest/extensions#extension-locations
//   Lifecycle/tool event hooks:
//     https://pi.dev/docs/latest/extensions#lifecycle-overview
//     https://pi.dev/docs/latest/extensions#tool-events
//   Installable package format:
//     https://pi.dev/docs/latest/packages#creating-a-pi-package
//
// This file is published as a Pi package from src/plugins/pi. The package.json
// `pi.extensions` manifest points Pi at this entrypoint.
//
// Wire contract:
//   POST /api/v1/telemetry/import on the Zeeq API, matching
//   Zeeq.Platform.Telemetry.Ingest.Import.AgentTelemetryImportRequest /
//   ImportedAgentEvent (src/backend/Zeeq.Platform.Telemetry/Ingest/Import).
//   Field names below are the exact wire keys (snake_case / gen_ai.* dotted
//   attributes) so the object literals ARE the payload shape — there is no
//   separate camelCase-to-wire mapping step to drift out of sync again.
//   Only three event kinds exist server-side: Prompt, ToolResult, Completion.
//
// Model/cost contract:
//   - Pi model IDs may be provider-qualified, such as openai-codex/gpt-5.5.
//     Export the model catalog key only, such as gpt-5.5, so Zeeq can match
//     its pricing catalog when provider-reported cost is absent.
//   - Pi's final assistant messages may include message.usage.cost.total. Export
//     that as zeeq.cost.usd because it is the harness/provider-normalized request
//     total. Zeeq persists it as authoritative and falls back to token-based
//     estimation (AgentTelemetryCostEnricher) only when it is absent.
//   - Provider errors can still include all-zero usage objects. Do not export
//     those as completion rows.
//   - Zeeq has no field for tool-call token counts on this import contract, so
//     they are tracked locally but never sent.
//
// Configuration via environment variables:
//   ZEEQ_BASE_URL               default: https://app.zeeq.ai
//   PI_TELEMETRY_ENDPOINT       optional full import URL override
//   ZEEQ_ACCESS_TOKEN           preferred; sent as Bearer token
//   LOCAL_ZEEQ_ACCESS_TOKEN     optional local fallback; sent as Bearer token
//   PI_TELEMETRY_API_KEY        optional legacy fallback; sent as Bearer token
//   PI_TELEMETRY_BATCH_SIZE     default: 10
//   PI_TELEMETRY_FLUSH_MS       default: 5000
//   PI_TELEMETRY_TIMEOUT_MS     default: 3000
//   PI_TELEMETRY_RETRY_MS       default: 30000
//   PI_TELEMETRY_LOG_ERRORS     true/1/yes logs export failures to stderr
//   PI_TELEMETRY_DISABLED       true/1/yes disables exporting
//   ZEEQ_OTEL_OMIT_PROMPT       true/1/yes exports "omit_by_config" for prompt_text
//   ZEEQ_OTEL_TRUNCATE_PROMPT   min: 16, default/0: no config truncation

type EventKind = "Prompt" | "ToolResult" | "Completion";

// NOTE: Matches Zeeq.Platform.Telemetry.Ingest.Import.ImportedAgentEvent
// field-for-field except for two intentional omissions: `user.email` and
// `organization_id`. Both exist on the server-side record, but
// AgentTelemetryImportHandler always overwrites/derives them from the
// authenticated bearer token server-side and ignores any client-supplied
// value, so there is nothing for this client to send. If this type looks
// short of the C# record's field list, check there first before assuming
// a contract drift.
type TelemetryEvent = {
  kind: EventKind;
  "event.id"?: string | null;
  "event.timestamp"?: string | null;
  prompt_text?: string | null;
  prompt_length?: number | null;
  "gen_ai.tool.name"?: string | null;
  "gen_ai.tool.call.id"?: string | null;
  "mcp.server.name"?: string | null;
  "gen_ai.tool.call.arguments"?: unknown;
  "gen_ai.tool.call.result"?: string | null;
  "gen_ai.tool.call.duration_ms"?: number | null;
  "gen_ai.tool.call.success"?: boolean | null;
  "gen_ai.request.model"?: string | null;
  "gen_ai.usage.input_tokens"?: number | null;
  "gen_ai.usage.cached_tokens"?: number | null;
  "gen_ai.usage.output_tokens"?: number | null;
  "gen_ai.usage.reasoning_tokens"?: number | null;
  "zeeq.cost.usd"?: number | null;
};

/** Matches Zeeq.Platform.Telemetry.Ingest.Import.AgentTelemetryImportRequest field-for-field. */
type TelemetryRequest = {
  conversation_id: string;
  harness_name: "pi";
  events: TelemetryEvent[];
  harness_version?: string | null;
  repository_remote_url?: string | null;
  head_branch?: string | null;
  head_sha?: string | null;
};

// Narrow structural views of Pi's runtime objects for the fields this file reads.
// Pi's real types (AgentMessage from @earendil-works/pi-agent-core, Model from
// @earendil-works/pi-ai) are not re-exported by @earendil-works/pi-coding-agent's
// public API, so importing them directly would mean depending on an undeclared
// transitive package that could change without notice. These local types trade
// full fidelity for a boundary that's at least checked against typos and shape
// drift, without pretending to fully pin down providers' varying usage payloads.
type PiModel = { provider?: string; id?: string; name?: string };
type PiMessageUsage = Record<string, unknown>;
type PiMessage = { usage?: PiMessageUsage };

const MAX_PROMPT_CHARS = 8_000;
const MAX_SNIPPET_CHARS = 4_000;
const MAX_TOOL_ARGS_CHARS = 4_000;
const DEFAULT_ZEEQ_BASE_URL = "https://app.zeeq.ai";
const TELEMETRY_IMPORT_PATH = "/api/v1/telemetry/import";
const OMITTED_PROMPT_TEXT = "omit_by_config";
const TRUNCATED_PROMPT_SUFFIX = "...(truncated_by_config)";

type ToolExecution = {
  timestamp: number;
  toolName: string;
  args: unknown;
  input: unknown;
  resultLogged: boolean;
};

export default function (pi: ExtensionAPI) {
  const endpoint = telemetryEndpoint();
  const disabled = /^(1|true|yes)$/i.test(process.env.PI_TELEMETRY_DISABLED ?? "");
  const bearerToken =
    process.env.ZEEQ_ACCESS_TOKEN
    ?? process.env.LOCAL_ZEEQ_ACCESS_TOKEN
    ?? process.env.PI_TELEMETRY_API_KEY;
  const batchSize = parsePositiveInt(process.env.PI_TELEMETRY_BATCH_SIZE, 10);
  const flushMs = parsePositiveInt(process.env.PI_TELEMETRY_FLUSH_MS, 5000);
  const timeoutMs = parsePositiveInt(process.env.PI_TELEMETRY_TIMEOUT_MS, 3000);
  const retryMs = parsePositiveInt(process.env.PI_TELEMETRY_RETRY_MS, 30_000);
  const logExportErrors = /^(1|true|yes)$/i.test(process.env.PI_TELEMETRY_LOG_ERRORS ?? "");
  const omitPrompt = /^(1|true|yes)$/i.test(process.env.ZEEQ_OTEL_OMIT_PROMPT ?? "");
  const promptTruncateChars = parsePromptTruncateChars(process.env.ZEEQ_OTEL_TRUNCATE_PROMPT);
  const processSessionId = randomUUID();

  let queue: TelemetryEvent[] = [];
  let flushTimer: NodeJS.Timeout | undefined;
  let nextFlushAfter = 0;
  let conversationId: string | null = null;
  let repositoryRemoteUrl: string | null = null;
  let headBranch: string | null = null;
  let headSha: string | null = null;
  let lastPrompt: string | null = null;
  let lastPromptRaw: string | null = null;
  let lastPromptLength: number | null = null;
  const toolExecutions = new Map<string, ToolExecution>();

  /** Returns the Zeeq pricing-catalog model key from Pi's event context when available. */
  function modelName(ctx?: { model?: unknown }): string | null {
    const model = ctx?.model as PiModel | undefined;
    if (!model) return null;
    if (model.id) return normalizeModelName(model.id);
    return normalizeModelName(model.name);
  }

  /** Common fields every exported event carries. */
  function baseEvent(kind: EventKind): TelemetryEvent {
    return {
      kind,
      "event.timestamp": new Date().toISOString(),
    };
  }

  /** Adds an event to the in-memory batch and schedules a best-effort export. */
  function enqueue(event: TelemetryEvent) {
    if (disabled) return;
    queue.push(event);
    if (queue.length >= batchSize && Date.now() >= nextFlushAfter) {
      void flush();
      return;
    }
    scheduleFlush(delayUntilNextFlush());
  }

  /** Schedules the next export without keeping the Node process alive solely for telemetry. */
  function scheduleFlush(delayMs = flushMs) {
    if (flushTimer) return;
    flushTimer = setTimeout(() => {
      flushTimer = undefined;
      void flush();
    }, delayMs);
    flushTimer.unref?.();
  }

  /** Returns the delay required to honor the retry backoff window. */
  function delayUntilNextFlush(): number {
    return Math.max(flushMs, nextFlushAfter - Date.now());
  }

  /** Posts the current batch to Zeeq's import API and never throws through Pi lifecycle hooks. */
  async function flush() {
    const activeConversationId = conversationId;
    if (disabled || queue.length === 0 || !activeConversationId) return;
    if (flushTimer) {
      clearTimeout(flushTimer);
      flushTimer = undefined;
    }

    const events = queue;
    queue = [];

    const body: TelemetryRequest = {
      conversation_id: activeConversationId,
      harness_name: "pi",
      repository_remote_url: repositoryRemoteUrl,
      head_branch: headBranch,
      head_sha: headSha,
      events,
    };

    try {
      const headers: Record<string, string> = { "content-type": "application/json" };
      if (bearerToken) headers.authorization = `Bearer ${bearerToken}`;

      const response = await fetch(endpoint, {
        method: "POST",
        headers,
        body: JSON.stringify(body),
        signal: timeoutSignal(timeoutMs),
      });

      if (!response.ok) {
        // Put events back once; avoid throwing from lifecycle hooks.
        handleExportFailure(events, `export failed: ${response.status} ${response.statusText}`);
        return;
      }

      nextFlushAfter = 0;
    } catch (error) {
      handleExportFailure(events, `export error: ${String(error)}`);
    }
  }

  /** Requeues failed exports with a capped backlog so temporary ingest failures do not break Pi. */
  function handleExportFailure(events: TelemetryEvent[], message: string) {
    // NOTE: eviction here is size-only (most-recent-first via the concat order,
    // then a hard cap), not age- or retry-count-based. Reviewed and accepted:
    // Pi sessions are short-lived interactive processes, not long-running
    // daemons, so an outage long enough for stale queued events to matter is
    // an edge case not worth the extra bookkeeping. The size cap already
    // guarantees this never grows unbounded.
    queue = events.concat(queue).slice(0, Math.max(batchSize * 5, 50));
    nextFlushAfter = Date.now() + retryMs;
    scheduleFlush(retryMs);
    if (logExportErrors) console.error(`[pi-telemetry] ${message}`);
  }

  // Session startup gives us the stable file path Pi uses to persist this conversation,
  // plus a one-time read of local git identity so Zeeq can link this conversation to a PR.
  pi.on("session_start", async (event, ctx) => {
    const sessionFile = ctx.sessionManager.getSessionFile?.() ?? null;
    conversationId = stableId(sessionFile ?? `${ctx.cwd}:${processSessionId}`);
    lastPrompt = null;
    lastPromptRaw = null;
    lastPromptLength = null;
    toolExecutions.clear();

    const git = readGitInfo(ctx.cwd);
    repositoryRemoteUrl = git.remoteUrl;
    headBranch = git.branch;
    headSha = git.sha;
  });

  // Capture the raw user prompt before Pi expands skills/templates or starts the agent loop.
  pi.on("input", async (event, ctx) => {
    const prompt = event.text ?? "";
    lastPromptRaw = prompt;
    lastPrompt = promptTextForTelemetry(prompt, omitPrompt, promptTruncateChars);
    lastPromptLength = prompt.length;
    enqueue({
      ...baseEvent("Prompt"),
      prompt_text: lastPrompt,
      prompt_length: lastPromptLength,
      "gen_ai.request.model": modelName(ctx),
    });
  });

  // Keep the latest prompt aligned for programmatic sends that bypass the interactive input event.
  pi.on("before_agent_start", async (event) => {
    // Covers prompts injected by extensions or paths that do not go through interactive input.
    if (event.prompt && event.prompt !== lastPromptRaw) {
      lastPromptRaw = event.prompt;
      lastPrompt = promptTextForTelemetry(event.prompt, omitPrompt, promptTruncateChars);
      lastPromptLength = event.prompt.length;
    }
  });

  // Tool execution start gives us arguments and a timestamp for duration calculation.
  pi.on("tool_execution_start", async (event) => {
    toolExecutions.set(event.toolCallId, {
      timestamp: Date.now(),
      toolName: event.toolName,
      args: event.args,
      input: event.args,
      resultLogged: false,
    });
  });

  // tool_call can arrive separately with the input Pi actually passes to the tool.
  pi.on("tool_call", async (event) => {
    const tracked = toolExecutions.get(event.toolCallId);
    if (tracked) {
      tracked.input = event.input;
      tracked.toolName = event.toolName;
      return;
    }

    toolExecutions.set(event.toolCallId, {
      timestamp: Date.now(),
      toolName: event.toolName,
      args: event.input,
      input: event.input,
      resultLogged: false,
    });
  });

  /** Emits one ToolResult event per tool call, regardless of whether Pi reports result and end events. */
  function recordToolResult(event: {
    toolCallId: string;
    toolName: string;
    input?: unknown;
    content?: unknown;
    result?: unknown;
    isError?: boolean;
  }) {
    const tracked = toolExecutions.get(event.toolCallId);
    if (tracked?.resultLogged) return;

    const output = event.content ?? resultContent(event.result);
    const content = contentToText(output);
    const input = event.input ?? tracked?.input ?? tracked?.args ?? null;

    if (tracked) tracked.resultLogged = true;

    enqueue({
      ...baseEvent("ToolResult"),
      "event.id": event.toolCallId,
      "gen_ai.tool.name": event.toolName,
      "gen_ai.tool.call.id": event.toolCallId,
      "gen_ai.tool.call.arguments": boundedJsonValue(input, MAX_TOOL_ARGS_CHARS),
      "gen_ai.tool.call.result": truncate(content, MAX_SNIPPET_CHARS),
      "gen_ai.tool.call.duration_ms": tracked ? Date.now() - tracked.timestamp : null,
      "gen_ai.tool.call.success": event.isError == null ? null : !event.isError,
    });
  }

  // Some tools report their output here. recordToolResult deduplicates with tool_execution_end.
  pi.on("tool_result", async (event) => {
    recordToolResult(event);
  });

  // Always close out tracked tool executions so the map does not grow across long sessions.
  pi.on("tool_execution_end", async (event) => {
    recordToolResult(event);
    toolExecutions.delete(event.toolCallId);
  });

  // Agent end is the point where Pi exposes the full message list, including model usage data.
  pi.on("agent_end", async (event, ctx) => {
    const tokens = collectTokens(event.messages);
    if (tokens) {
      enqueue({
        ...baseEvent("Completion"),
        "gen_ai.request.model": modelName(ctx),
        "gen_ai.usage.input_tokens": tokens.inputTokenCount,
        "gen_ai.usage.cached_tokens": tokens.cachedInputTokenCount,
        "gen_ai.usage.output_tokens": tokens.outputTokenCount,
        "gen_ai.usage.reasoning_tokens": tokens.reasoningTokenCount,
        "zeeq.cost.usd": collectCostUsd(event.messages),
      });
    }
    await flush();
  });

  // Flush during shutdown so the last turn is not left waiting for the batch timer.
  pi.on("session_shutdown", async () => {
    await flush();
  });

  // Manual escape hatch for debugging: /telemetry-flush forces the current queue to export.
  pi.registerCommand("telemetry-flush", {
    description: "Flush queued Pi lifecycle telemetry events",
    handler: async (_args, ctx) => {
      await flush();
      ctx.ui.notify("Telemetry flushed", "info");
    },
  });
}

/** Resolves the Zeeq import URL from a full endpoint override or a base URL. */
function telemetryEndpoint(): string {
  const endpointOverride = process.env.PI_TELEMETRY_ENDPOINT?.trim();
  if (endpointOverride) return endpointOverride;

  const baseUrl = normalizeBaseUrl(process.env.ZEEQ_BASE_URL?.trim() || DEFAULT_ZEEQ_BASE_URL);
  return `${baseUrl}${TELEMETRY_IMPORT_PATH}`;
}

/** Accepts either a full URL or a bare custom domain and removes trailing slashes. */
function normalizeBaseUrl(value: string): string {
  const withScheme = /^https?:\/\//i.test(value) ? value : `https://${value}`;
  return withScheme.replace(/\/+$/, "");
}

/** Converts Pi provider-qualified model names into Zeeq's pricing catalog key shape. */
function normalizeModelName(value: string | null | undefined): string | null {
  if (!value) return null;
  const trimmed = value.trim();
  if (!trimmed) return null;
  const separatorIndex = trimmed.lastIndexOf("/");
  return separatorIndex >= 0 ? trimmed.slice(separatorIndex + 1) : trimmed;
}

/** Parses positive integer env vars while falling back for empty, malformed, or zero values. */
function parsePositiveInt(value: string | undefined, fallback: number): number {
  const parsed = Number.parseInt(value ?? "", 10);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback;
}

/** Parses prompt text privacy truncation where 0 means "do not truncate by config". */
function parsePromptTruncateChars(value: string | undefined): number | null {
  const parsed = Number.parseInt(value ?? "", 10);
  if (!Number.isFinite(parsed) || parsed <= 0) return null;
  if (parsed < 16) return 16;
  return parsed;
}

/** Creates Zeeq-sized deterministic ids from Pi session paths without storing local paths as ids. */
function stableId(input: string): string {
  return createHash("sha256").update(input).digest("hex").slice(0, 32);
}

/** Reads local git remote/branch/sha once at session start so Zeeq can link the conversation to a PR. */
function readGitInfo(cwd: string): {
  remoteUrl: string | null;
  branch: string | null;
  sha: string | null;
} {
  const run = (args: string[]): string | null => {
    try {
      return (
        execFileSync("git", args, { cwd, encoding: "utf8", stdio: ["ignore", "pipe", "ignore"] }).trim()
        || null
      );
    } catch {
      return null;
    }
  };

  return {
    remoteUrl: run(["config", "--get", "remote.origin.url"]),
    branch: run(["rev-parse", "--abbrev-ref", "HEAD"]),
    sha: run(["rev-parse", "HEAD"]),
  };
}

/** Applies prompt-text privacy settings while preserving `prompt_length` as the raw length. */
function promptTextForTelemetry(
  value: string | null | undefined,
  omitPrompt: boolean,
  configTruncateChars: number | null,
): string | null {
  if (value == null) return null;
  if (omitPrompt) return OMITTED_PROMPT_TEXT;
  if (configTruncateChars !== null) {
    return value.length > configTruncateChars
      ? `${value.slice(0, configTruncateChars)}${TRUNCATED_PROMPT_SUFFIX}`
      : value;
  }
  return truncate(value, MAX_PROMPT_CHARS);
}

/** Bounds prompt and output fields before they enter the import payload. */
function truncate(value: string | null | undefined, max: number): string | null {
  if (value == null) return null;
  if (value.length <= max) return value;
  return `${value.slice(0, max)}…[truncated ${value.length - max} chars]`;
}

/**
 * Bounds a value destined for a JSON-typed field (Zeeq's `gen_ai.tool.call.arguments`
 * is a raw JSON element, not a string) without breaking payload validity the way
 * truncating a stringified blob would.
 */
function boundedJsonValue(value: unknown, max: number): unknown {
  if (value == null) return null;
  try {
    const json = JSON.stringify(value);
    if (json.length <= max) return value;
    return { truncated: true, originalLength: json.length };
  } catch {
    return { unserializable: true };
  }
}

/** Serializes arbitrary Pi event payloads for bounded text snippets without throwing on cycles. */
function safeJson(value: unknown, max: number): string | null {
  if (value == null) return null;
  try {
    return truncate(JSON.stringify(value), max);
  } catch {
    return "[unserializable]";
  }
}

/** Uses AbortSignal.timeout when present, with a Node-compatible fallback for older runtimes. */
function timeoutSignal(timeoutMs: number): AbortSignal | undefined {
  if (typeof AbortSignal.timeout === "function") return AbortSignal.timeout(timeoutMs);

  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), timeoutMs);
  timeout.unref?.();
  return controller.signal;
}

/** Flattens Pi tool result content into the bounded text snippet Zeeq stores. */
function contentToText(content: unknown): string {
  if (typeof content === "string") return content;
  if (!Array.isArray(content)) return safeJson(content, MAX_SNIPPET_CHARS) ?? "";
  return content
    .map((part) => {
      if (typeof part === "string") return part;
      if (part && typeof part === "object" && "text" in part) {
        return String((part as { text?: unknown }).text ?? "");
      }
      return safeJson(part, 500) ?? "";
    })
    .filter(Boolean)
    .join("\n");
}

/** Extracts the `content` field from tool result objects while preserving primitive results. */
function resultContent(result: unknown): unknown {
  if (result && typeof result === "object" && "content" in result) {
    return (result as { content?: unknown }).content;
  }
  return result;
}

/** Sums provider usage metadata from Pi's final messages into Zeeq's token summary shape. */
function collectTokens(messages: readonly unknown[] | undefined): {
  cachedInputTokenCount: number;
  inputTokenCount: number;
  outputTokenCount: number;
  reasoningTokenCount: number;
  toolTokenCount: number;
} | null {
  const totals = {
    cachedInputTokenCount: 0,
    inputTokenCount: 0,
    outputTokenCount: 0,
    reasoningTokenCount: 0,
    toolTokenCount: 0,
  };
  let found = false;
  let hasNonZeroUsage = false;

  for (const message of messages ?? []) {
    const usage = messageUsage(message);
    if (!usage) continue;
    found = true;
    totals.cachedInputTokenCount += numberFrom(usage, ["cacheReadInputTokens", "cachedInputTokens", "cacheRead", "cachedInputTokenCount"]);
    totals.inputTokenCount += numberFrom(usage, ["inputTokens", "promptTokens", "input", "inputTokenCount"]);
    totals.outputTokenCount += numberFrom(usage, ["outputTokens", "completionTokens", "output", "outputTokenCount"]);
    totals.reasoningTokenCount += numberFrom(usage, ["reasoningTokens", "thinkingTokens", "reasoning", "reasoningTokenCount"]);
    totals.toolTokenCount += numberFrom(usage, ["toolTokens", "toolUseTokens", "tool", "toolTokenCount"]);
    hasNonZeroUsage ||= Object.values(totals).some((value) => value > 0);
  }

  return found && hasNonZeroUsage ? totals : null;
}

/** Sums Pi's provider-normalized request costs when available on final messages. */
function collectCostUsd(messages: readonly unknown[] | undefined): number | null {
  let total = 0;
  let found = false;

  for (const message of messages ?? []) {
    const cost = readCostTotal(messageUsage(message));
    if (cost === null) continue;
    total += cost;
    found = true;
  }

  return found ? total : null;
}

/**
 * Narrows an unknown Pi message (from a discriminated union not exported by
 * @earendil-works/pi-coding-agent's public API — see the PiMessage note above)
 * down to its `usage` bag, when present.
 */
function messageUsage(message: unknown): PiMessageUsage | undefined {
  if (!message || typeof message !== "object") return undefined;
  const usage = (message as PiMessage).usage;
  return usage && typeof usage === "object" ? usage : undefined;
}

/** Reads a positive `usage.cost.total` out of an otherwise-untyped usage bag. */
function readCostTotal(usage: PiMessageUsage | undefined): number | null {
  const cost = usage?.cost;
  if (!cost || typeof cost !== "object") return null;
  const total = (cost as Record<string, unknown>).total;
  return typeof total === "number" && Number.isFinite(total) && total > 0 ? total : null;
}

/** Reads the first supported numeric usage property from a provider-specific usage object. */
function numberFrom(source: Record<string, unknown>, keys: string[]): number {
  for (const key of keys) {
    const value = source[key];
    if (typeof value === "number" && Number.isFinite(value)) return value;
  }
  return 0;
}
