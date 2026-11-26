import { readFileSync, writeFileSync, mkdirSync, renameSync, readdirSync, statSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { homedir } from 'node:os';
import { createHash } from 'node:crypto';
import { spawn, spawnSync } from 'node:child_process';
import { mapTranscript } from './mapper.mjs';

const data = process.env.PLUGIN_DATA;
const truthy = s => /^(1|true|yes)$/i.test(s || '');
function error(message) {
  try { if (data) { mkdirSync(data, { recursive: true }); writeFileSync(join(data, 'lastError'), String(message).slice(0, 1000), { mode: 0o600 }); } } catch {}
  if (truthy(process.env.ANTIGRAVITY_TELEMETRY_LOG_ERRORS)) console.error(message);
}
function token() {
  if (process.env.ZEEQ_ACCESS_TOKEN || process.env.LOCAL_ZEEQ_ACCESS_TOKEN) return process.env.ZEEQ_ACCESS_TOKEN || process.env.LOCAL_ZEEQ_ACCESS_TOKEN;
  try { const file = join(homedir(), '.config/zeeq/credentials'); if ((statSync(file).mode & 0o077) !== 0) return; return readFileSync(file, 'utf8').trim(); } catch {}
}
function save(path, value) { const tmp = `${path}.${process.pid}.tmp`; writeFileSync(tmp, JSON.stringify(value), { mode: 0o600 }); renameSync(tmp, path); }
function git(path, ...args) { try { return spawnSync('git', ['-C', path, ...args], { encoding: 'utf8', timeout: 1000 }).stdout?.trim() || undefined; } catch {} }
function statePath(id) { if (!/^[a-zA-Z0-9_-]+$/.test(id)) throw Error('Invalid conversation id'); return join(data, 'state', `${id}.json`); }
function load(path) { try { return JSON.parse(readFileSync(path, 'utf8')); } catch { return {}; } }
async function deliver(id, credential) {
  const metaPath = statePath(id), path = `${metaPath}.delivery`, lock = `${path}.lock`;
  let acquired = false;
  for (let attempt = 0; attempt < 100 && !acquired; attempt++) {
    try { mkdirSync(lock); writeFileSync(join(lock, 'owner'), String(process.pid)); acquired = true; } catch {
      try {
        if (Date.now() - statSync(lock).mtimeMs > 2000) {
          const owner = Number(readFileSync(join(lock, 'owner'), 'utf8'));
          try { process.kill(owner, 0); } catch (e) { if (e.code === 'ESRCH') rmSync(lock, { recursive: true, force: true }); }
        }
      } catch (e) { if (e.code === 'ENOENT') { try { if (Date.now() - statSync(lock).mtimeMs > 2000) rmSync(lock, { recursive: true, force: true }); } catch {} } }
      if (!acquired) await new Promise(resolve => setTimeout(resolve, 100));
    }
  }
  if (!acquired) throw Error('Telemetry delivery lock timed out');
  try {
    const state = { ...load(metaPath), ...load(path) };
    if (!state.transcriptPath) return;
    const content = readFileSync(state.transcriptPath);
    const firstEnd = content.indexOf(10);
    if (firstEnd < 0) return;
    const firstLine = createHash('sha256').update(content.subarray(0, firstEnd)).digest('hex');
    let offset = state.byteOffset || 0;
    if (offset > content.length || (state.firstLine && state.firstLine !== firstLine)) { offset = 0; state.mapper = {}; }
    const lastEnd = content.lastIndexOf(10);
    if (lastEnd + 1 < offset) { offset = 0; state.mapper = {}; }
    const delta = content.subarray(offset, lastEnd + 1).toString('utf8');
    const rows = delta.split('\n').filter(Boolean).map(line => JSON.parse(line));
    const mapper = structuredClone(state.mapper || {});
    const events = mapTranscript(id, rows, { state: mapper, allowExpiry: lastEnd + 1 === content.length, model: state.model, omitPrompt: truthy(process.env.ZEEQ_OTEL_OMIT_PROMPT), truncatePrompt: Number(process.env.ZEEQ_OTEL_TRUNCATE_PROMPT) || undefined });
    const endpoint = process.env.ANTIGRAVITY_TELEMETRY_ENDPOINT || `${(process.env.ZEEQ_BASE_URL || 'https://app.zeeq.ai').replace(/\/$/, '')}/api/v1/telemetry/import`;
    async function post(batch) {
      if (!batch.length) return;
      const response = await fetch(endpoint, { method: 'POST', headers: { 'content-type': 'application/json', authorization: `Bearer ${credential}` }, body: JSON.stringify({ conversation_id: id, harness_name: 'antigravity', harness_version: state.agyVersion, repository_remote_url: state.repositoryRemoteUrl, head_branch: state.headBranch, head_sha: state.headSha, events: batch }), signal: AbortSignal.timeout(Number(process.env.ANTIGRAVITY_TELEMETRY_TIMEOUT_MS) || 3000) });
      if (response.ok) return;
      if ([400, 413, 422].includes(response.status)) {
        if (batch.length === 1) { error(`Dropped invalid telemetry event (${response.status})`); return; }
        const middle = Math.floor(batch.length / 2); await post(batch.slice(0, middle)); await post(batch.slice(middle)); return;
      }
      if (response.status === 401 || response.status === 403) { save(path, { ...load(path), lastAuthError: response.status, authFailedAt: Date.now(), failedCredentialHash: createHash('sha256').update(credential).digest('hex') }); }
      throw Error(`Telemetry request failed: ${response.status}`);
    }
    for (let i = 0; i < events.length; i += 500) await post(events.slice(i, i + 500));
    save(path, { byteOffset: lastEnd + 1, firstLine, mapper });
  } finally { rmSync(lock, { recursive: true, force: true }); }
}
async function main() {
  if (!data || truthy(process.env.ANTIGRAVITY_TELEMETRY_DISABLED)) return;
  const credential = token(); if (!credential) return;
  mkdirSync(join(data, 'state'), { recursive: true });
  const action = process.argv[2];
  if (action === 'worker') { await deliver(process.argv[3], credential); return; }
  const payload = JSON.parse(readFileSync(0, 'utf8')); const id = payload.conversationId; if (!id) return;
  const path = statePath(id), state = load(path), workspace = payload.workspacePaths?.[0];
  Object.assign(state, { transcriptPath: payload.transcriptPath || state.transcriptPath, model: payload.modelName || state.model, lastSeen: Date.now() });
  if (action === 'SessionStart' && !state.agyVersion) { const version = spawnSync('agy', ['--version'], { encoding: 'utf8', timeout: 1000 }); state.agyVersion = version.stdout?.trim().slice(0, 100); }
  if (workspace && action === 'SessionStart') Object.assign(state, { repositoryRemoteUrl: git(workspace, 'remote', 'get-url', 'origin')?.replace(/(\w+:\/\/)[^/@]+@/, '$1'), headBranch: git(workspace, 'branch', '--show-current'), headSha: git(workspace, 'rev-parse', 'HEAD') });
  save(path, state);
  const launch = other => { const child = spawn(process.execPath, [process.argv[1], 'worker', other], { detached: true, stdio: 'ignore', env: process.env }); child.unref(); };
  if (action === 'SessionStart') {
    // Prune only checkpointed, complete transcripts; unsent or pending state stays.
    for (const file of readdirSync(join(data, 'state')).filter(file => file.endsWith('.json'))) {
      const metaPath = join(data, 'state', file), deliveryPath = metaPath + '.delivery', lock = deliveryPath + '.lock';
      try {
        const meta = load(metaPath); if (!meta.lastSeen || Date.now() - meta.lastSeen < 30 * 86400000) continue;
        mkdirSync(lock); writeFileSync(join(lock, 'owner'), String(process.pid));
        try {
          const latest = load(metaPath), delivery = load(deliveryPath), transcript = readFileSync(latest.transcriptPath);
          const firstEnd = transcript.indexOf(10);
          const firstLine = firstEnd < 0 ? null : createHash('sha256').update(transcript.subarray(0, firstEnd)).digest('hex');
          if (latest.lastSeen === meta.lastSeen && statSync(latest.transcriptPath).mtimeMs <= latest.lastSeen && delivery.byteOffset === transcript.length && firstLine === delivery.firstLine && !Object.keys(delivery.mapper?.pending || {}).length && !(delivery.mapper?.calls || []).length) {
            rmSync(deliveryPath, { force: true }); rmSync(metaPath, { force: true });
          }
        } finally { rmSync(lock, { recursive: true, force: true }); }
      } catch { /* Keep state when it cannot be verified as fully delivered. */ }
    }
    const credentialHash = createHash('sha256').update(credential).digest('hex');
    const recoverable = readdirSync(join(data, 'state')).filter(file => file.endsWith('.json')).map(file => ({ file, state: load(join(data, 'state', file)), delivery: load(join(data, 'state', file + '.delivery')) })).filter(({ state, delivery }) => {
      if (Date.now() - state.lastSeen <= 60000 || (delivery.failedCredentialHash === credentialHash && Date.now() - (delivery.authFailedAt || 0) < 3600000)) return false;
      try { const transcript = readFileSync(state.transcriptPath); const firstEnd = transcript.indexOf(10); const changed = firstEnd >= 0 && delivery.firstLine && createHash('sha256').update(transcript.subarray(0, firstEnd)).digest('hex') !== delivery.firstLine; const completeBytes = transcript.lastIndexOf(10) + 1; const expiredPending = completeBytes === transcript.length && Object.values(delivery.mapper?.pending || {}).some(call => Date.now() - Date.parse(call.row.created_at) > 86400000); return changed || completeBytes !== (delivery.byteOffset || 0) || expiredPending; } catch { return false; }
    }).sort((a, b) => (a.state.lastSeen || 0) - (b.state.lastSeen || 0)).slice(0, 10);
    for (const { file } of recoverable) launch(file.slice(0, -5));
  }
  if (action === 'Stop') { if (truthy(process.env.ANTIGRAVITY_TELEMETRY_INLINE)) await deliver(id, credential); else launch(id); }
}
try { await main(); } catch (e) { error(e instanceof SyntaxError ? 'Invalid transcript or hook payload JSON' : e.message); }
if (process.argv[2] !== 'worker') console.log(process.argv[2] === 'Stop' ? '{"decision":"stop"}' : '{}');
