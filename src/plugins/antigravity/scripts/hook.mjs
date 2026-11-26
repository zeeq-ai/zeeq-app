// src/hook.mjs
import { readFileSync, writeFileSync, mkdirSync, renameSync, readdirSync, statSync, rmSync } from "node:fs";
import { join } from "node:path";
import { homedir } from "node:os";
import { createHash } from "node:crypto";
import { spawn, spawnSync } from "node:child_process";

// src/mapper.mjs
var modelName = (value) => typeof value === "string" ? value.split("/").pop().replace(/-(low|medium|high|xhigh)$/i, "").slice(0, 128) : void 0;
var bounded = (value, limit) => String(value ?? "").slice(0, limit);
var identity = (conversation, row, index, kind) => ({ kind, "event.id": `${conversation}:${row.step_index}:${index}:${kind}`, "event.timestamp": row.created_at });
function mapTranscript(conversation, rows, options = {}) {
  const events = [];
  const calls = structuredClone(options.state?.calls ?? []);
  const pending = new Map(Object.entries(structuredClone(options.state?.pending ?? {})));
  const expired = new Set(options.state?.expired ?? []);
  const result = (call, content) => {
    const exit = /(?:Command exited with code|exit code)\s+(\d+)/i.exec(content);
    const created = /Created At:\s*([^\n]+)/i.exec(content);
    const completed = /Completed At:\s*([^\n]+)/i.exec(content);
    const duration = created && completed ? Date.parse(completed[1]) - Date.parse(created[1]) : NaN;
    const args = JSON.stringify(call.args ?? {});
    events.push({ ...identity(conversation, call.row, call.index, "ToolResult"), "gen_ai.tool.name": bounded(call.name, 512), "gen_ai.tool.call.id": `${conversation}:${call.row.step_index}:${call.index}`, "gen_ai.tool.call.arguments": args.length > 4e3 ? { truncated: true, originalLength: args.length } : call.args, "gen_ai.tool.call.result": bounded(content, 4e3), "gen_ai.tool.call.success": exit ? Number(exit[1]) === 0 : null, ...Number.isFinite(duration) && duration >= 0 && duration <= 2147483647 ? { "gen_ai.tool.call.duration_ms": duration } : {} });
  };
  for (let i = 0; i < rows.length; i++) {
    const row = rows[i];
    if (!Number.isInteger(row.step_index) || !row.created_at) continue;
    if (row.type === "USER_INPUT") {
      const raw = String(row.content ?? "").split("<ADDITIONAL_METADATA>")[0].replace(/^\s*<USER_REQUEST>\s*/, "").replace(/\s*<\/USER_REQUEST>\s*$/, "");
      const next = rows.slice(i + 1).find((r) => r.type === "PLANNER_RESPONSE");
      const limit = Math.min(8e3, Math.max(16, options.truncatePrompt || 8e3));
      events.push({ ...identity(conversation, row, 0, "Prompt"), prompt_text: options.omitPrompt ? "omit_by_config" : bounded(raw, limit), prompt_length: raw.length, "gen_ai.request.model": modelName(next?.model || options.model) });
    } else if (row.type === "PLANNER_RESPONSE") {
      (row.tool_calls ?? []).forEach((call, index) => calls.push({ ...call, row, index }));
      const cached = Number(row.cache_read_tokens) || 0;
      const fresh = Number(row.input_tokens) || 0;
      const output = Number(row.output_tokens) || 0;
      if (fresh || cached || output) events.push({ ...identity(conversation, row, 0, "Completion"), "gen_ai.request.model": modelName(row.model), "gen_ai.usage.input_tokens": Number(row.prompt_tokens ?? row.total_input_tokens ?? fresh + cached), "gen_ai.usage.cached_tokens": cached, "gen_ai.usage.output_tokens": output });
    } else if (row.type === "GENERIC") {
      const content = String(row.content ?? "");
      if (row.source !== "MODEL" || !/^Created At:/m.test(content)) continue;
      const task = /task id[:\s]+[\"]?([^\s\"]+)/i.exec(content)?.[1];
      if (task && expired.has(task)) continue;
      if (task && pending.has(task) && row.status === "RUNNING") continue;
      if (task && pending.has(task)) {
        result(pending.get(task), content);
        pending.delete(task);
        expired.add(task);
        continue;
      }
      if (!calls.length) continue;
      const call = calls.shift();
      if (row.status === "RUNNING" && task) pending.set(task, call);
      else {
        result(call, content);
        if (task) expired.add(task);
      }
    } else if (row.type === "SYSTEM_MESSAGE") {
      const content = String(row.content ?? "");
      const task = /task id[:\s]+[\"]?([^\s\"]+)/i.exec(content)?.[1];
      if (task && pending.has(task)) {
        result(pending.get(task), content);
        pending.delete(task);
        expired.add(task);
      }
    }
  }
  for (const [task, call] of pending) {
    if (options.allowExpiry !== false && (options.now ?? Date.now()) - Date.parse(call.row.created_at) > 24 * 60 * 60 * 1e3) {
      result(call, "[no completion observed]");
      pending.delete(task);
      expired.add(task);
    }
  }
  if (options.state) {
    options.state.calls = calls;
    options.state.pending = Object.fromEntries(pending);
    options.state.expired = [...expired];
  }
  return events;
}

// src/hook.mjs
var data = process.env.PLUGIN_DATA;
var truthy = (s) => /^(1|true|yes)$/i.test(s || "");
function error(message) {
  try {
    if (data) {
      mkdirSync(data, { recursive: true });
      writeFileSync(join(data, "lastError"), String(message).slice(0, 1e3), { mode: 384 });
    }
  } catch {
  }
  if (truthy(process.env.ANTIGRAVITY_TELEMETRY_LOG_ERRORS)) console.error(message);
}
function token() {
  if (process.env.ZEEQ_ACCESS_TOKEN || process.env.LOCAL_ZEEQ_ACCESS_TOKEN) return process.env.ZEEQ_ACCESS_TOKEN || process.env.LOCAL_ZEEQ_ACCESS_TOKEN;
  try {
    const file = join(homedir(), ".config/zeeq/credentials");
    if ((statSync(file).mode & 63) !== 0) return;
    return readFileSync(file, "utf8").trim();
  } catch {
  }
}
function save(path, value) {
  const tmp = `${path}.${process.pid}.tmp`;
  writeFileSync(tmp, JSON.stringify(value), { mode: 384 });
  renameSync(tmp, path);
}
function git(path, ...args) {
  try {
    return spawnSync("git", ["-C", path, ...args], { encoding: "utf8", timeout: 1e3 }).stdout?.trim() || void 0;
  } catch {
  }
}
function statePath(id) {
  if (!/^[a-zA-Z0-9_-]+$/.test(id)) throw Error("Invalid conversation id");
  return join(data, "state", `${id}.json`);
}
function load(path) {
  try {
    return JSON.parse(readFileSync(path, "utf8"));
  } catch {
    return {};
  }
}
async function deliver(id, credential) {
  const metaPath = statePath(id), path = `${metaPath}.delivery`, lock = `${path}.lock`;
  let acquired = false;
  for (let attempt = 0; attempt < 100 && !acquired; attempt++) {
    try {
      mkdirSync(lock);
      writeFileSync(join(lock, "owner"), String(process.pid));
      acquired = true;
    } catch {
      try {
        if (Date.now() - statSync(lock).mtimeMs > 2e3) {
          const owner = Number(readFileSync(join(lock, "owner"), "utf8"));
          try {
            process.kill(owner, 0);
          } catch (e) {
            if (e.code === "ESRCH") rmSync(lock, { recursive: true, force: true });
          }
        }
      } catch (e) {
        if (e.code === "ENOENT") {
          try {
            if (Date.now() - statSync(lock).mtimeMs > 2e3) rmSync(lock, { recursive: true, force: true });
          } catch {
          }
        }
      }
      if (!acquired) await new Promise((resolve) => setTimeout(resolve, 100));
    }
  }
  if (!acquired) throw Error("Telemetry delivery lock timed out");
  try {
    const state = { ...load(metaPath), ...load(path) };
    if (!state.transcriptPath) return;
    const content = readFileSync(state.transcriptPath);
    const firstEnd = content.indexOf(10);
    if (firstEnd < 0) return;
    const firstLine = createHash("sha256").update(content.subarray(0, firstEnd)).digest("hex");
    let offset = state.byteOffset || 0;
    if (offset > content.length || state.firstLine && state.firstLine !== firstLine) {
      offset = 0;
      state.mapper = {};
    }
    const lastEnd = content.lastIndexOf(10);
    if (lastEnd + 1 < offset) {
      offset = 0;
      state.mapper = {};
    }
    const delta = content.subarray(offset, lastEnd + 1).toString("utf8");
    const rows = [];
    for (const line of delta.split("\n").filter(Boolean)) {
      try {
        rows.push(JSON.parse(line));
      } catch {
        error("Skipped malformed transcript row");
      }
    }
    const mapper = structuredClone(state.mapper || {});
    const events = mapTranscript(id, rows, { state: mapper, allowExpiry: lastEnd + 1 === content.length, model: state.model, omitPrompt: truthy(process.env.ZEEQ_OTEL_OMIT_PROMPT), truncatePrompt: Number(process.env.ZEEQ_OTEL_TRUNCATE_PROMPT) || void 0 });
    if (!events.length) {
      save(path, { byteOffset: lastEnd + 1, firstLine, mapper });
      return;
    }
    const endpoint = process.env.ANTIGRAVITY_TELEMETRY_ENDPOINT || `${(process.env.ZEEQ_BASE_URL || "https://app.zeeq.ai").replace(/\/$/, "")}/api/v1/telemetry/import`;
    const pluginVersion = "0.1.0";
    const rejectionKey = `${firstLine}:${offset}`;
    const previous = load(path);
    if (previous.suspendedUntil > Date.now() && previous.suspendedVersion === pluginVersion) return;
    async function post(batch) {
      const response = await fetch(endpoint, { method: "POST", headers: { "content-type": "application/json", authorization: `Bearer ${credential}` }, body: JSON.stringify({ conversation_id: id, harness_name: "antigravity", harness_version: state.agyVersion, repository_remote_url: state.repositoryRemoteUrl, head_branch: state.headBranch, head_sha: state.headSha, events: batch }), signal: AbortSignal.timeout(Number(process.env.ANTIGRAVITY_TELEMETRY_TIMEOUT_MS) || 3e3) });
      if (response.ok) return response.status;
      if ([400, 413, 422].includes(response.status)) return response.status;
      if (response.status === 401 || response.status === 403) save(path, { ...load(path), lastAuthError: response.status, authFailedAt: Date.now(), failedCredentialHash: createHash("sha256").update(credential).digest("hex") });
      throw Error(`Telemetry request failed: ${response.status}`);
    }
    let attempts = previous.rejectionKey === rejectionKey ? previous.rejectionAttempts || 0 : 0;
    let scan = previous.rejectionKey === rejectionKey ? previous.rejectionScan : void 0;
    if (attempts < 3) {
      let rejectedStatus;
      for (let i = 0; i < events.length; i += 500) {
        const status = await post(events.slice(i, i + 500));
        if (status >= 400) {
          rejectedStatus = status;
          break;
        }
      }
      if (rejectedStatus) {
        attempts++;
        save(path, { ...previous, rejectionKey, rejectionAttempts: attempts });
        error(`Telemetry batch rejected (${rejectedStatus}); checkpoint retained (attempt ${attempts})`);
        if (attempts < 3) return;
      }
    }
    if (attempts >= 3) {
      scan ||= { accepted: [], rejected: [] };
      const seen = /* @__PURE__ */ new Set([...scan.accepted, ...scan.rejected.map((item) => item.id)]);
      let count = 0;
      for (const event of events) {
        const eventId = event["event.id"];
        if (seen.has(eventId)) continue;
        if (count++ >= 200) break;
        const status = await post([event]);
        if (status < 400) scan.accepted.push(eventId);
        else scan.rejected.push({ id: eventId, status });
        save(path, { ...load(path), rejectionKey, rejectionAttempts: attempts, rejectionScan: scan });
      }
      if (scan.accepted.length + scan.rejected.length < events.length) return;
      if (scan.rejected.length && !scan.accepted.length) {
        save(path, { ...load(path), rejectionKey, rejectionAttempts: 0, rejectionScan: void 0, suspendedUntil: Date.now() + 864e5, suspendedVersion: pluginVersion });
        error("All isolated telemetry events rejected; delivery suspended for 24 hours");
        return;
      }
      for (const item of scan.rejected) error(`Dropped invalid telemetry event ${item.id} (${item.status})`);
    }
    save(path, { byteOffset: lastEnd + 1, firstLine, mapper });
  } finally {
    rmSync(lock, { recursive: true, force: true });
  }
}
async function main() {
  if (!data || truthy(process.env.ANTIGRAVITY_TELEMETRY_DISABLED)) return;
  const credential = token();
  if (!credential) return;
  mkdirSync(join(data, "state"), { recursive: true });
  const action = process.argv[2];
  if (action === "worker") {
    await deliver(process.argv[3], credential);
    return;
  }
  const payload = JSON.parse(readFileSync(0, "utf8"));
  const id = payload.conversationId;
  if (!id) return;
  const path = statePath(id), metaLock = `${path}.meta.lock`, workspace = payload.workspacePaths?.[0];
  let locked = false;
  for (let i = 0; i < 100 && !locked; i++) {
    try {
      mkdirSync(metaLock);
      writeFileSync(join(metaLock, "owner"), String(process.pid));
      locked = true;
    } catch {
      try {
        if (Date.now() - statSync(metaLock).mtimeMs > 2e3) {
          const owner = Number(readFileSync(join(metaLock, "owner"), "utf8"));
          try {
            process.kill(owner, 0);
          } catch (e) {
            if (e.code === "ESRCH") rmSync(metaLock, { recursive: true, force: true });
          }
        }
      } catch (e) {
        if (e.code === "ENOENT") {
          try {
            if (Date.now() - statSync(metaLock).mtimeMs > 2e3) rmSync(metaLock, { recursive: true, force: true });
          } catch {
          }
        }
      }
      if (!locked) await new Promise((resolve) => setTimeout(resolve, 10));
    }
  }
  if (!locked) throw Error("Telemetry metadata lock timed out");
  try {
    const state = load(path);
    Object.assign(state, { transcriptPath: payload.transcriptPath || state.transcriptPath, model: payload.modelName || state.model, lastSeen: Date.now() });
    if (action === "SessionStart" && !state.agyVersion) {
      const version = spawnSync("agy", ["--version"], { encoding: "utf8", timeout: 1e3 });
      state.agyVersion = version.stdout?.trim().slice(0, 100);
    }
    if (workspace && action === "SessionStart") Object.assign(state, { repositoryRemoteUrl: git(workspace, "remote", "get-url", "origin")?.replace(/(\w+:\/\/)[^/@]+@/, "$1"), headBranch: git(workspace, "branch", "--show-current"), headSha: git(workspace, "rev-parse", "HEAD") });
    save(path, state);
  } finally {
    rmSync(metaLock, { recursive: true, force: true });
  }
  const launch = (other) => {
    const child = spawn(process.execPath, [process.argv[1], "worker", other], { detached: true, stdio: "ignore", env: process.env });
    child.unref();
  };
  if (action === "SessionStart") {
    for (const file of readdirSync(join(data, "state")).filter((file2) => file2.endsWith(".json"))) {
      const metaPath = join(data, "state", file), deliveryPath = metaPath + ".delivery", lock = deliveryPath + ".lock";
      try {
        const meta = load(metaPath);
        if (!meta.lastSeen || Date.now() - meta.lastSeen < 30 * 864e5) continue;
        mkdirSync(lock);
        writeFileSync(join(lock, "owner"), String(process.pid));
        try {
          const metaLock2 = metaPath + ".meta.lock";
          try {
            mkdirSync(metaLock2);
            writeFileSync(join(metaLock2, "owner"), String(process.pid));
          } catch {
            continue;
          }
          try {
            const latest = load(metaPath), delivery = load(deliveryPath), transcript = readFileSync(latest.transcriptPath);
            const firstEnd = transcript.indexOf(10);
            const firstLine = firstEnd < 0 ? null : createHash("sha256").update(transcript.subarray(0, firstEnd)).digest("hex");
            if (latest.lastSeen === meta.lastSeen && statSync(latest.transcriptPath).mtimeMs <= latest.lastSeen && delivery.byteOffset === transcript.length && firstLine === delivery.firstLine && !Object.keys(delivery.mapper?.pending || {}).length && !(delivery.mapper?.calls || []).length) {
              rmSync(deliveryPath, { force: true });
              rmSync(metaPath, { force: true });
            }
          } finally {
            rmSync(metaLock2, { recursive: true, force: true });
          }
        } finally {
          rmSync(lock, { recursive: true, force: true });
        }
      } catch {
      }
    }
    const credentialHash = createHash("sha256").update(credential).digest("hex");
    const recoverable = readdirSync(join(data, "state")).filter((file) => file.endsWith(".json")).map((file) => ({ file, state: load(join(data, "state", file)), delivery: load(join(data, "state", file + ".delivery")) })).filter(({ state, delivery }) => {
      if (delivery.suspendedUntil > Date.now() && delivery.suspendedVersion === "0.1.0" || Date.now() - state.lastSeen <= 6e4 || delivery.failedCredentialHash === credentialHash && Date.now() - (delivery.authFailedAt || 0) < 36e5) return false;
      try {
        const transcript = readFileSync(state.transcriptPath);
        const firstEnd = transcript.indexOf(10);
        const changed = firstEnd >= 0 && delivery.firstLine && createHash("sha256").update(transcript.subarray(0, firstEnd)).digest("hex") !== delivery.firstLine;
        const completeBytes = transcript.lastIndexOf(10) + 1;
        const expiredPending = completeBytes === transcript.length && Object.values(delivery.mapper?.pending || {}).some((call) => Date.now() - Date.parse(call.row.created_at) > 864e5);
        return changed || completeBytes !== (delivery.byteOffset || 0) || expiredPending;
      } catch {
        return false;
      }
    }).sort((a, b) => (a.state.lastSeen || 0) - (b.state.lastSeen || 0)).slice(0, 10);
    for (const { file } of recoverable) launch(file.slice(0, -5));
  }
  if (action === "Stop") {
    if (truthy(process.env.ANTIGRAVITY_TELEMETRY_INLINE)) await deliver(id, credential);
    else launch(id);
  }
}
try {
  await main();
} catch (e) {
  error(e instanceof SyntaxError ? "Invalid transcript or hook payload JSON" : e.message);
}
if (process.argv[2] !== "worker") console.log(process.argv[2] === "Stop" ? '{"decision":"stop"}' : "{}");
