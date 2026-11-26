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
    if (offset > content.length || (state.firstLine && state.firstLine !== firstLine) || (state.prefixHash && createHash('sha256').update(content.subarray(0, offset)).digest('hex') !== state.prefixHash)) { offset = 0; state.mapper = {}; }
    const lastEnd = content.lastIndexOf(10);
    if (lastEnd + 1 < offset) { offset = 0; state.mapper = {}; }
    const delta = content.subarray(offset, lastEnd + 1).toString('utf8');
    const rows = [];
    for (const line of delta.split('\n').filter(Boolean)) { try { rows.push(JSON.parse(line)); } catch { error('Skipped malformed transcript row'); } }
    const mapper = structuredClone(state.mapper || {});
    const events = mapTranscript(id, rows, { state: mapper, allowExpiry: lastEnd + 1 === content.length, model: state.model, omitPrompt: truthy(process.env.ZEEQ_OTEL_OMIT_PROMPT), truncatePrompt: Number(process.env.ZEEQ_OTEL_TRUNCATE_PROMPT) || undefined });
    if (!events.length) {
      const previous = load(path);
      if (previous.rejectionKey === `${firstLine}:${offset}` && previous.rejectionAttempts && previous.rejectionEnd <= content.length && previous.rejectionHash === createHash('sha256').update(content.subarray(offset, previous.rejectionEnd)).digest('hex')) return;
      if (previous.rejectionAttempts) error('Transcript changed before rejected events could be delivered');
      save(path, { byteOffset: lastEnd + 1, firstLine, prefixHash: createHash('sha256').update(content.subarray(0, lastEnd + 1)).digest('hex'), mapper }); return;
    }
    const endpoint = process.env.ANTIGRAVITY_TELEMETRY_ENDPOINT || `${(process.env.ZEEQ_BASE_URL || 'https://app.zeeq.ai').replace(/\/$/, '')}/api/v1/telemetry/import`;
    const pluginVersion = '0.1.0';
    const rejectionKey = `${firstLine}:${offset}`;
    const previous = load(path);
    if (previous.suspendedUntil > Date.now() && previous.suspendedVersion === pluginVersion) return;
    async function post(batch) {
      const response = await fetch(endpoint, { method: 'POST', headers: { 'content-type': 'application/json', authorization: `Bearer ${credential}` }, body: JSON.stringify({ conversation_id: id, harness_name: 'antigravity', harness_version: state.agyVersion, repository_remote_url: state.repositoryRemoteUrl, head_branch: state.headBranch, head_sha: state.headSha, events: batch }), signal: AbortSignal.timeout(Number(process.env.ANTIGRAVITY_TELEMETRY_TIMEOUT_MS) || 3000) });
      if (response.ok) return response.status;
      if ([400, 413, 422].includes(response.status)) return response.status;
      if (response.status === 401 || response.status === 403) save(path, { ...load(path), lastAuthError: response.status, authFailedAt: Date.now(), failedCredentialHash: createHash('sha256').update(credential).digest('hex') });
      throw Error(`Telemetry request failed: ${response.status}`);
    }
    const sameRejected = previous.rejectionKey === rejectionKey && previous.rejectionEnd <= content.length && previous.rejectionHash === createHash('sha256').update(content.subarray(offset, previous.rejectionEnd)).digest('hex');
    if (previous.rejectionAttempts && !sameRejected) error('Transcript changed before rejected events could be delivered');
    let attempts = sameRejected ? (previous.rejectionAttempts || 0) : 0;
    let scan = sameRejected ? previous.rejectionScan : undefined;
    const rejectionEnd = sameRejected ? previous.rejectionEnd : lastEnd + 1;
    const rejectionHash = createHash('sha256').update(content.subarray(offset, rejectionEnd)).digest('hex');
    if (attempts < 3) {
      let rejectedStatus;
      for (let i = 0; i < events.length; i += 500) {
        const status = await post(events.slice(i, i + 500));
        if (status >= 400) { rejectedStatus = status; break; }
      }
      if (rejectedStatus) {
        attempts++;
        save(path, { ...previous, rejectionKey, rejectionEnd, rejectionHash, rejectionAttempts: attempts });
        error(`Telemetry batch rejected (${rejectedStatus}); checkpoint retained (attempt ${attempts})`);
        if (attempts < 3) return;
      }
    }
    if (attempts >= 3) {
      scan ||= { accepted: [], rejected: [], fingerprints: {} };
      const fingerprints = Object.fromEntries(events.map(event => [event['event.id'], createHash('sha256').update(JSON.stringify(event)).digest('hex')]));
      if ([...scan.accepted, ...scan.rejected.map(item => item.id)].some(id => scan.fingerprints?.[id] !== fingerprints[id])) scan = { accepted: [], rejected: [], fingerprints: {} };
      const seen = new Set([...scan.accepted, ...scan.rejected.map(item => item.id)]);
      let count = 0;
      for (const event of events) {
        const eventId = event['event.id'];
        if (seen.has(eventId)) continue;
        if (count++ >= 200) break;
        const status = await post([event]);
        if (status < 400) scan.accepted.push(eventId); else scan.rejected.push({ id: eventId, status });
        scan.fingerprints[eventId] = fingerprints[eventId];
        save(path, { ...load(path), rejectionKey, rejectionEnd, rejectionHash, rejectionAttempts: attempts, rejectionScan: scan });
      }
      const classified = new Set([...scan.accepted, ...scan.rejected.map(item => item.id)]);
      if (events.some(event => !classified.has(event['event.id']))) return;
      if (scan.rejected.length && !scan.accepted.length) {
        save(path, { ...load(path), rejectionKey, rejectionAttempts: 0, rejectionScan: undefined, suspendedUntil: Date.now() + 86400000, suspendedVersion: pluginVersion });
        error('All isolated telemetry events rejected; delivery suspended for 24 hours');
        return;
      }
      for (const item of scan.rejected) error(`Dropped invalid telemetry event ${item.id} (${item.status})`);
    }
    save(path, { byteOffset: lastEnd + 1, firstLine, prefixHash: createHash('sha256').update(content.subarray(0, lastEnd + 1)).digest('hex'), mapper });
  } finally { rmSync(lock, { recursive: true, force: true }); }
}
async function main() {
  if (!data || truthy(process.env.ANTIGRAVITY_TELEMETRY_DISABLED)) return;
  const credential = token(); if (!credential) return;
  mkdirSync(join(data, 'state'), { recursive: true });
  const action = process.argv[2];
  if (action === 'worker') { await deliver(process.argv[3], credential); return; }
  const payload = JSON.parse(readFileSync(0, 'utf8')); const id = payload.conversationId; if (!id) return;
  const path = statePath(id), metaLock = `${path}.meta.lock`, workspace = payload.workspacePaths?.[0];
  let locked = false;
  for (let i = 0; i < 100 && !locked; i++) {
    try { mkdirSync(metaLock); writeFileSync(join(metaLock, 'owner'), String(process.pid)); locked = true; }
    catch {
      try { if (Date.now() - statSync(metaLock).mtimeMs > 2000) { const owner = Number(readFileSync(join(metaLock, 'owner'), 'utf8')); try { process.kill(owner, 0); } catch (e) { if (e.code === 'ESRCH') rmSync(metaLock, { recursive: true, force: true }); } } } catch (e) { if (e.code === 'ENOENT') { try { if (Date.now() - statSync(metaLock).mtimeMs > 2000) rmSync(metaLock, { recursive: true, force: true }); } catch {} } }
      if (!locked) await new Promise(resolve => setTimeout(resolve, 10));
    }
  }
  if (!locked) throw Error('Telemetry metadata lock timed out');
  try {
  const state = load(path);
  Object.assign(state, { transcriptPath: payload.transcriptPath || state.transcriptPath, model: payload.modelName || state.model, lastSeen: Date.now() });
  if (action === 'SessionStart' && !state.agyVersion) { const version = spawnSync('agy', ['--version'], { encoding: 'utf8', timeout: 1000 }); state.agyVersion = version.stdout?.trim().slice(0, 100); }
  if (workspace && action === 'SessionStart') Object.assign(state, { repositoryRemoteUrl: git(workspace, 'remote', 'get-url', 'origin')?.replace(/(\w+:\/\/)[^/@]+@/, '$1'), headBranch: git(workspace, 'branch', '--show-current'), headSha: git(workspace, 'rev-parse', 'HEAD') });
  save(path, state);
  } finally { rmSync(metaLock, { recursive: true, force: true }); }
  const launch = other => { const child = spawn(process.execPath, [process.argv[1], 'worker', other], { detached: true, stdio: 'ignore', env: process.env }); child.unref(); };
  if (action === 'SessionStart') {
    // Prune only checkpointed, complete transcripts; unsent or pending state stays.
    for (const file of readdirSync(join(data, 'state')).filter(file => file.endsWith('.json'))) {
      const metaPath = join(data, 'state', file), deliveryPath = metaPath + '.delivery', lock = deliveryPath + '.lock';
      try {
        const meta = load(metaPath); if (!meta.lastSeen || Date.now() - meta.lastSeen < 30 * 86400000) continue;
        mkdirSync(lock); writeFileSync(join(lock, 'owner'), String(process.pid));
        try {
          const metaLock = metaPath + '.meta.lock';
          try { mkdirSync(metaLock); writeFileSync(join(metaLock, 'owner'), String(process.pid)); } catch { continue; }
          try {
          const latest = load(metaPath), delivery = load(deliveryPath), transcript = readFileSync(latest.transcriptPath);
          const firstEnd = transcript.indexOf(10);
          const firstLine = firstEnd < 0 ? null : createHash('sha256').update(transcript.subarray(0, firstEnd)).digest('hex');
          if (latest.lastSeen === meta.lastSeen && statSync(latest.transcriptPath).mtimeMs <= latest.lastSeen && delivery.byteOffset === transcript.length && firstLine === delivery.firstLine && (!delivery.prefixHash || delivery.prefixHash === createHash('sha256').update(transcript).digest('hex')) && !Object.keys(delivery.mapper?.pending || {}).length && !(delivery.mapper?.calls || []).length) {
            rmSync(deliveryPath, { force: true }); rmSync(metaPath + '.recovery', { force: true }); rmSync(metaPath, { force: true });
          }
          } finally { rmSync(metaLock, { recursive: true, force: true }); }
        } finally { rmSync(lock, { recursive: true, force: true }); }
      } catch { /* Keep state when it cannot be verified as fully delivered. */ }
    }
    const credentialHash = createHash('sha256').update(credential).digest('hex');
    const recoverable = readdirSync(join(data, 'state')).filter(file => file.endsWith('.json')).map(file => ({ file, state: load(join(data, 'state', file)), delivery: load(join(data, 'state', file + '.delivery')), recoveryAt: Number(load(join(data, 'state', file + '.recovery')).at) || 0 })).filter(({ state, delivery }) => {
      if ((delivery.suspendedUntil > Date.now() && delivery.suspendedVersion === '0.1.0') || Date.now() - state.lastSeen <= 60000 || (delivery.failedCredentialHash === credentialHash && Date.now() - (delivery.authFailedAt || 0) < 3600000)) return false;
      try { const transcript = readFileSync(state.transcriptPath); const firstEnd = transcript.indexOf(10); const changed = firstEnd >= 0 && delivery.firstLine && createHash('sha256').update(transcript.subarray(0, firstEnd)).digest('hex') !== delivery.firstLine; const completeBytes = transcript.lastIndexOf(10) + 1; const prefixChanged = delivery.prefixHash && createHash('sha256').update(transcript.subarray(0, delivery.byteOffset || 0)).digest('hex') !== delivery.prefixHash; const expiredPending = completeBytes === transcript.length && [...Object.values(delivery.mapper?.pending || {}), ...(delivery.mapper?.calls || [])].some(call => Date.now() - Date.parse(call.row.created_at) > 86400000); return changed || prefixChanged || completeBytes !== (delivery.byteOffset || 0) || expiredPending; } catch { return false; }
    }).sort((a, b) => a.recoveryAt - b.recoveryAt || (a.state.lastSeen || 0) - (b.state.lastSeen || 0)).slice(0, 10);
    for (const { file } of recoverable) { save(join(data, 'state', file + '.recovery'), { at: Date.now() }); launch(file.slice(0, -5)); }
  }
  if (action === 'Stop') { if (truthy(process.env.ANTIGRAVITY_TELEMETRY_INLINE)) await deliver(id, credential); else launch(id); }
}
try { await main(); } catch (e) { error(e instanceof SyntaxError ? 'Invalid transcript or hook payload JSON' : e.message); }
if (process.argv[2] !== 'worker') console.log(process.argv[2] === 'Stop' ? '{"decision":"stop"}' : '{}');
