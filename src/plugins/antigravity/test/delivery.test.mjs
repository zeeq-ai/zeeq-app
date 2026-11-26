import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, writeFileSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { createServer } from 'node:http';
import { spawn } from 'node:child_process';
import { once } from 'node:events';

const hook = resolve('scripts/hook.mjs');
async function invoke(env, action, payload) {
  const child = spawn(process.execPath, [hook, action], { env: { ...process.env, ...env }, stdio: ['pipe', 'pipe', 'pipe'] });
  child.stdin.end(JSON.stringify(payload));
  const [code] = await once(child, 'exit'); assert.equal(code, 0);
}

test('retries failed delivery, isolates poison, and checkpoints complete lines', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'zeeq-test-'));
  const transcript = join(dir, 'transcript');
  const row = (step, text) => JSON.stringify({ step_index: step, created_at: `2026-01-01T00:00:0${step}Z`, type: 'USER_INPUT', content: text });
  writeFileSync(transcript, `${row(0, 'good')}\n${row(1, 'poison')}\n${row(2, 'last')}\n{"partial":`);
  let mode = 'fail'; const received = [];
  const server = createServer(async (req, res) => { let body = ''; for await (const c of req) body += c; const events = JSON.parse(body).events; received.push(events); res.statusCode = mode === 'fail' ? 503 : events.some(e => e.prompt_text === 'poison') ? 400 : 202; res.end(); });
  server.listen(0, '127.0.0.1'); await once(server, 'listening');
  const env = { PLUGIN_DATA: dir, ZEEQ_ACCESS_TOKEN: 'test', ANTIGRAVITY_TELEMETRY_INLINE: '1', ANTIGRAVITY_TELEMETRY_ENDPOINT: `http://127.0.0.1:${server.address().port}` };
  const payload = { conversationId: 'test', transcriptPath: transcript };
  try {
    await invoke(env, 'Stop', payload);
    assert.equal(received.length, 1);
    mode = 'ok'; await invoke(env, 'Stop', payload);
    assert.ok(received.some(events => events.length === 1 && events[0].prompt_text === 'poison'));
    const count = received.length; await invoke(env, 'Stop', payload); assert.equal(received.length, count);
    const state = JSON.parse(readFileSync(join(dir, 'state/test.json.delivery'), 'utf8'));
    assert.equal(state.byteOffset, Buffer.byteLength(`${row(0, 'good')}\n${row(1, 'poison')}\n${row(2, 'last')}\n`));
  } finally { server.close(); rmSync(dir, { recursive: true, force: true }); }
});

test('records authentication failure and preserves cursor for retry', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'zeeq-auth-'));
  const transcript = join(dir, 'transcript');
  writeFileSync(transcript, JSON.stringify({ step_index: 0, created_at: '2026-01-01T00:00:00Z', type: 'USER_INPUT', content: 'hello' }) + '\n');
  let status = 401; let requests = 0;
  const server = createServer((req, res) => { requests++; req.resume(); res.statusCode = status; res.end(); });
  server.listen(0, '127.0.0.1'); await once(server, 'listening');
  const env = { PLUGIN_DATA: dir, ZEEQ_ACCESS_TOKEN: 'test', ANTIGRAVITY_TELEMETRY_INLINE: '1', ANTIGRAVITY_TELEMETRY_ENDPOINT: `http://127.0.0.1:${server.address().port}` };
  const payload = { conversationId: 'auth', transcriptPath: transcript };
  try {
    await invoke(env, 'Stop', payload);
    let state = JSON.parse(readFileSync(join(dir, 'state/auth.json.delivery'), 'utf8'));
    assert.equal(state.lastAuthError, 401); assert.equal(state.byteOffset, undefined);
    status = 202; await invoke(env, 'Stop', payload);
    state = JSON.parse(readFileSync(join(dir, 'state/auth.json.delivery'), 'utf8'));
    assert.equal(state.lastAuthError, undefined); assert.ok(state.byteOffset > 0); assert.equal(requests, 2);
  } finally { server.close(); rmSync(dir, { recursive: true, force: true }); }
});

test('recovers inactive conversations beyond recent sessions after credentials change', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'zeeq-recovery-'));
  const stateDir = join(dir, 'state');
  const { mkdirSync, existsSync } = await import('node:fs');
  const { createHash } = await import('node:crypto');
  mkdirSync(stateDir);
  const transcript = join(dir, 'transcript');
  writeFileSync(transcript, JSON.stringify({ step_index: 0, created_at: '2026-01-01T00:00:00Z', type: 'USER_INPUT', content: 'recovery' }) + '\n');
  writeFileSync(join(stateDir, 'old.json'), JSON.stringify({ transcriptPath: transcript, lastSeen: Date.now() - 120000 }));
  writeFileSync(join(stateDir, 'old.json.delivery'), JSON.stringify({ lastAuthError: 401, failedCredentialHash: createHash('sha256').update('expired').digest('hex') }));
  for (let i = 0; i < 12; i++) writeFileSync(join(stateDir, `recent${i}.json`), JSON.stringify({ transcriptPath: transcript, lastSeen: Date.now() }));
  let received = 0;
  const server = createServer((req, res) => { received++; req.resume(); res.statusCode = 202; res.end(); });
  server.listen(0, '127.0.0.1'); await once(server, 'listening');
  const env = { PLUGIN_DATA: dir, ZEEQ_ACCESS_TOKEN: 'new-token', ANTIGRAVITY_TELEMETRY_ENDPOINT: `http://127.0.0.1:${server.address().port}` };
  try {
    await invoke(env, 'SessionStart', { conversationId: 'current', transcriptPath: transcript });
    for (let i = 0; i < 50 && !received; i++) await new Promise(resolve => setTimeout(resolve, 20));
    assert.equal(received, 1);
    for (let i = 0; i < 100 && existsSync(join(stateDir, 'old.json.delivery.lock')); i++) await new Promise(resolve => setTimeout(resolve, 20));
    assert.equal(existsSync(join(stateDir, 'old.json.delivery.lock')), false);
  } finally { server.close(); rmSync(dir, { recursive: true, force: true }); }
});

test('prunes only old fully delivered state and keeps unsent state', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'zeeq-prune-'));
  const { mkdirSync, existsSync, utimesSync } = await import('node:fs');
  const { createHash } = await import('node:crypto');
  const stateDir = join(dir, 'state'); mkdirSync(stateDir);
  const transcript = join(dir, 'transcript');
  const line = JSON.stringify({ type: 'USER_INPUT', step_index: 0, created_at: '2026-01-01T00:00:00Z', content: 'fixture' });
  writeFileSync(transcript, line + '\n');
  const firstLine = createHash('sha256').update(line).digest('hex');
  const meta = { transcriptPath: transcript, lastSeen: Date.now() - 31 * 86400000 };
  utimesSync(transcript, new Date(meta.lastSeen - 1000), new Date(meta.lastSeen - 1000));
  writeFileSync(join(stateDir, 'delivered.json'), JSON.stringify(meta));
  writeFileSync(join(stateDir, 'delivered.json.delivery'), JSON.stringify({ byteOffset: Buffer.byteLength(line + '\n'), firstLine, mapper: {} }));
  writeFileSync(join(stateDir, 'unsent.json'), JSON.stringify(meta));
  writeFileSync(join(stateDir, 'unsent.json.delivery'), JSON.stringify({ byteOffset: 0, failedCredentialHash: createHash('sha256').update('test').digest('hex'), authFailedAt: Date.now() }));
  try {
    await invoke({ PLUGIN_DATA: dir, ZEEQ_ACCESS_TOKEN: 'test' }, 'SessionStart', { conversationId: 'current' });
    assert.equal(existsSync(join(stateDir, 'delivered.json')), false);
    assert.equal(existsSync(join(stateDir, 'unsent.json')), true);
  } finally { rmSync(dir, { recursive: true, force: true }); }
});

test('recovers first complete row without a checkpoint and a replaced transcript', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'zeeq-rewrite-'));
  const { mkdirSync, existsSync } = await import('node:fs');
  const stateDir = join(dir, 'state'); mkdirSync(stateDir);
  const line = JSON.stringify({ type: 'USER_INPUT', step_index: 0, created_at: '2026-01-01T00:00:00Z', content: 'new' });
  const transcript = join(dir, 'transcript'); writeFileSync(transcript, line + '\n');
  const meta = { transcriptPath: transcript, lastSeen: Date.now() - 120000 };
  writeFileSync(join(stateDir, 'first.json'), JSON.stringify(meta));
  writeFileSync(join(stateDir, 'rewrite.json'), JSON.stringify(meta));
  writeFileSync(join(stateDir, 'rewrite.json.delivery'), JSON.stringify({ byteOffset: Buffer.byteLength(line + '\n'), firstLine: 'old-hash' }));
  const received = [];
  const server = createServer(async (req, res) => { let body = ''; for await (const c of req) body += c; received.push(JSON.parse(body).conversation_id); res.statusCode = 202; res.end(); });
  server.listen(0, '127.0.0.1'); await once(server, 'listening');
  try {
    await invoke({ PLUGIN_DATA: dir, ZEEQ_ACCESS_TOKEN: 'test', ANTIGRAVITY_TELEMETRY_ENDPOINT: `http://127.0.0.1:${server.address().port}` }, 'SessionStart', { conversationId: 'current' });
    for (let i = 0; i < 100 && (received.length < 2 || existsSync(join(stateDir, 'first.json.delivery.lock')) || existsSync(join(stateDir, 'rewrite.json.delivery.lock'))); i++) await new Promise(resolve => setTimeout(resolve, 20));
    assert.deepEqual(received.sort(), ['first', 'rewrite']);
  } finally { server.close(); rmSync(dir, { recursive: true, force: true }); }
});

test('does not expire a pending task while its next row is incomplete', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'zeeq-partial-'));
  const transcript = join(dir, 'transcript');
  const rows = [{ type: 'PLANNER_RESPONSE', step_index: 0, created_at: '2020-01-01T00:00:00Z', tool_calls: [{ name: 'run', args: {} }] }, { type: 'GENERIC', source: 'MODEL', status: 'RUNNING', step_index: 1, created_at: '2020-01-01T00:00:01Z', content: 'Created At: now\ntask id: task-1' }];
  const completion = JSON.stringify({ type: 'SYSTEM_MESSAGE', step_index: 2, created_at: '2020-01-01T00:00:02Z', content: 'Task id "task-1" finished: Command exited with code 0' });
  writeFileSync(transcript, rows.map(JSON.stringify).join('\n') + '\n' + completion.slice(0, 30));
  const received = [];
  const server = createServer(async (req, res) => { let body = ''; for await (const c of req) body += c; received.push(...JSON.parse(body).events); res.statusCode = 202; res.end(); });
  server.listen(0, '127.0.0.1'); await once(server, 'listening');
  const env = { PLUGIN_DATA: dir, ZEEQ_ACCESS_TOKEN: 'test', ANTIGRAVITY_TELEMETRY_INLINE: '1', ANTIGRAVITY_TELEMETRY_ENDPOINT: `http://127.0.0.1:${server.address().port}` };
  const payload = { conversationId: 'partial', transcriptPath: transcript };
  try {
    await invoke(env, 'Stop', payload); assert.equal(received.length, 0);
    writeFileSync(transcript, rows.map(JSON.stringify).join('\n') + '\n' + completion + '\n');
    await invoke(env, 'Stop', payload); assert.equal(received.length, 1); assert.equal(received[0]['gen_ai.tool.call.success'], true);
  } finally { server.close(); rmSync(dir, { recursive: true, force: true }); }
});
