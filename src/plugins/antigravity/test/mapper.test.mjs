import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mapTranscript } from '../src/mapper.mjs';

test('maps prompts and usage deterministically', () => {
  const rows = [{ step_index: 0, created_at: '2026-01-01T00:00:00Z', type: 'USER_INPUT', content: '<USER_REQUEST>\nhello\n</USER_REQUEST>\n<ADDITIONAL_METADATA>secret' }, { step_index: 1, created_at: '2026-01-01T00:00:01Z', type: 'PLANNER_RESPONSE', model: 'google/gemini-low', input_tokens: 2, cache_read_tokens: 3, output_tokens: 1 }];
  const events = mapTranscript('conversation', rows);
  assert.equal(events[0].prompt_text, 'hello');
  assert.equal(events[0]['gen_ai.request.model'], 'gemini');
  assert.equal(events[1]['gen_ai.usage.input_tokens'], 5);
  assert.deepEqual(events, mapTranscript('conversation', rows));
});

test('pairs a background result with its original call', () => {
  const rows = [{ step_index: 0, created_at: '2026-01-01T00:00:00Z', type: 'PLANNER_RESPONSE', tool_calls: [{ name: 'run', args: {} }] }, { step_index: 1, created_at: '2026-01-01T00:00:01Z', type: 'GENERIC', source: 'MODEL', status: 'RUNNING', content: 'Created At: now\ntask id: task-1' }, { step_index: 2, created_at: '2026-01-01T00:00:02Z', type: 'SYSTEM_MESSAGE', content: 'Task id \"task-1\" exit code 0' }];
  assert.equal(mapTranscript('c', rows)[0]['event.id'], 'c:0:0:ToolResult');
  assert.equal(mapTranscript('c', rows)[0]['gen_ai.tool.call.success'], true);
});

test('keeps background calls across incremental reads', () => {
  const state = {};
  const call = { step_index: 1, created_at: '2026-01-01T00:00:00Z', type: 'PLANNER_RESPONSE', tool_calls: [{ name: 'run', args: {} }] };
  const running = { step_index: 2, created_at: '2026-01-01T00:00:01Z', type: 'GENERIC', source: 'MODEL', status: 'RUNNING', content: 'Created At: now\ntask id: task-1' };
  assert.equal(mapTranscript('c', [call, running], { state, now: Date.parse('2026-01-01T00:00:03Z') }).length, 0);
  const result = { step_index: 3, created_at: '2026-01-01T00:00:02Z', type: 'SYSTEM_MESSAGE', content: 'Task id \"task-1\" Command exited with code 0' };
  assert.equal(mapTranscript('c', [result], { state, now: Date.parse('2026-01-01T00:00:03Z') })[0]['event.timestamp'], call.created_at);
  assert.equal(mapTranscript('c', [result], { state, now: Date.parse('2026-01-01T00:00:03Z') }).length, 0);
});

test('omits prompt content and bounds tool data', () => {
  const rows = [{ step_index: 0, created_at: '2026-01-01T00:00:00Z', type: 'USER_INPUT', content: 'private' }, { step_index: 1, created_at: '2026-01-01T00:00:01Z', type: 'PLANNER_RESPONSE', tool_calls: [{ name: 'run', args: { value: 'x'.repeat(5000) } }] }, { step_index: 2, created_at: '2026-01-01T00:00:02Z', type: 'GENERIC', source: 'MODEL', content: 'Created At: now\n' + 'y'.repeat(5000) }];
  const [prompt, tool] = mapTranscript('c', rows, { omitPrompt: true });
  assert.equal(prompt.prompt_text, 'omit_by_config');
  assert.equal(prompt.prompt_length, 7);
  assert.equal(tool['gen_ai.tool.call.arguments'].truncated, true);
  assert.equal(tool['gen_ai.tool.call.result'].length, 4000);
});

test('does not consume queued calls for unrelated generic rows', () => {
  const rows = [{ step_index: 0, created_at: '2026-01-01T00:00:00Z', type: 'PLANNER_RESPONSE', tool_calls: [{ name: 'run', args: {} }] }, { step_index: 1, created_at: '2026-01-01T00:00:01Z', type: 'GENERIC', source: 'SYSTEM', content: 'progress' }, { step_index: 2, created_at: '2026-01-01T00:00:02Z', type: 'GENERIC', source: 'MODEL', content: 'Created At: now\nCommand exited with code 0' }];
  const events = mapTranscript('c', rows);
  assert.equal(events.length, 1);
  assert.equal(events[0]['gen_ai.tool.call.success'], true);
});

test('maps sanitized captured transcripts', async () => {
  const { readFileSync } = await import('node:fs');
  for (const name of ['plain', 'tool', 'background', 'read-file', 'multi-tool']) {
    const rows = readFileSync(new URL(`./fixtures/${name}.jsonl`, import.meta.url), 'utf8').trim().split('\n').map(JSON.parse);
    const events = mapTranscript('fixture', rows);
    assert.ok(events.some(event => event.kind === 'Prompt'));
    assert.ok(events.some(event => event.kind === 'Completion'));
    if (name !== 'plain') assert.ok(events.some(event => event.kind === 'ToolResult' && event['gen_ai.tool.call.success'] === true));
    assert.deepEqual(events, mapTranscript('fixture', rows));
  }
});

test('matches the complete background task ID', () => {
  const state = { calls: [], pending: {
    'task-1': { name: 'first', args: {}, row: { step_index: 1, created_at: '2026-01-01T00:00:00Z' }, index: 0 },
    'task-10': { name: 'second', args: {}, row: { step_index: 2, created_at: '2026-01-01T00:00:00Z' }, index: 0 },
  } };
  const events = mapTranscript('c', [{ step_index: 3, created_at: '2026-01-01T00:00:03Z', type: 'SYSTEM_MESSAGE', content: 'Task id "task-10" finished with result: Command exited with code 0.' }], { state, now: Date.parse('2026-01-01T00:00:03Z') });
  assert.equal(events[0]['gen_ai.tool.name'], 'second');
  assert.ok(state.pending['task-1']);
});

test('does not assign a late expired task to another call', () => {
  const state = { pending: { 'task-1': { name: 'old', args: {}, row: { step_index: 0, created_at: '2026-01-01T00:00:00Z' }, index: 0 } }, calls: [] };
  const now = Date.parse('2026-01-03T00:00:00Z');
  assert.equal(mapTranscript('c', [], { state, now })[0]['gen_ai.tool.call.result'], '[no completion observed]');
  state.calls.push({ name: 'new', args: {}, row: { step_index: 2, created_at: '2026-01-03T00:00:00Z' }, index: 0 });
  const late = { type: 'GENERIC', source: 'MODEL', status: 'RUNNING', step_index: 3, created_at: '2026-01-03T00:00:00Z', content: 'Created At: now\ntask id: task-1' };
  assert.equal(mapTranscript('c', [late], { state, now }).length, 0);
  assert.equal(state.calls[0].name, 'new');
});

test('pairs a completed task result when its running row was not observed', () => {
  const rows = [{ type: 'PLANNER_RESPONSE', step_index: 0, created_at: '2026-01-01T00:00:00Z', tool_calls: [{ name: 'run', args: {} }] }, { type: 'GENERIC', source: 'MODEL', status: 'DONE', step_index: 1, created_at: '2026-01-01T00:00:01Z', content: 'Created At: now\ntask id: task-1\nCommand exited with code 0' }];
  const state = {}; const events = mapTranscript('c', rows, { state });
  assert.equal(events.length, 1); assert.equal(events[0]['gen_ai.tool.name'], 'run'); assert.equal(events[0]['gen_ai.tool.call.success'], true); assert.equal(state.calls.length, 0);
});

test('leaves calls queued for unmatched system completions', () => {
  const rows = [{ type: 'PLANNER_RESPONSE', step_index: 0, created_at: '2026-01-01T00:00:00Z', tool_calls: [{ name: 'run', args: {} }] }, { type: 'SYSTEM_MESSAGE', step_index: 1, created_at: '2026-01-01T00:00:01Z', content: 'Task id "task-1" finished with result: Command exited with code 0.' }];
  const state = {}; const events = mapTranscript('c', rows, { state });
  assert.equal(events.length, 0); assert.equal(state.calls.length, 1);
});

test('ignores a repeated running marker until completion', () => {
  const state = {}; const now = Date.parse('2026-01-01T00:00:03Z');
  const call = { type: 'PLANNER_RESPONSE', step_index: 0, created_at: '2026-01-01T00:00:00Z', tool_calls: [{ name: 'run', args: {} }] };
  const running = { type: 'GENERIC', source: 'MODEL', status: 'RUNNING', step_index: 1, created_at: '2026-01-01T00:00:01Z', content: 'Created At: now\ntask id: task-1' };
  assert.equal(mapTranscript('c', [call, running, { ...running, step_index: 2 }], { state, now }).length, 0);
  assert.ok(state.pending['task-1']);
});
