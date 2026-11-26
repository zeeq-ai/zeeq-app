// Transcript rows are replayed from the beginning. Stable identities let the
// server deduplicate delivery while preserving pending tool calls across stops.
const modelName = value => typeof value === 'string' ? value.split('/').pop().replace(/-(low|medium|high|xhigh)$/i, '').slice(0, 128) : undefined;
const bounded = (value, limit) => String(value ?? '').slice(0, limit);
const identity = (conversation, row, index, kind) => ({ kind, 'event.id': `${conversation}:${row.step_index}:${index}:${kind}`, 'event.timestamp': row.created_at });

export function mapTranscript(conversation, rows, options = {}) {
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
    events.push({ ...identity(conversation, call.row, call.index, 'ToolResult'), 'gen_ai.tool.name': bounded(call.name, 512), 'gen_ai.tool.call.id': `${conversation}:${call.row.step_index}:${call.index}`, 'gen_ai.tool.call.arguments': args.length > 4000 ? { truncated: true, originalLength: args.length } : call.args, 'gen_ai.tool.call.result': bounded(content, 4000), 'gen_ai.tool.call.success': exit ? Number(exit[1]) === 0 : null, ...(Number.isFinite(duration) && duration >= 0 && duration <= 2147483647 ? { 'gen_ai.tool.call.duration_ms': duration } : {}) });
  };
  for (let i = 0; i < rows.length; i++) {
    const row = rows[i];
    if (!Number.isInteger(row.step_index) || !row.created_at) continue;
    if (row.type === 'USER_INPUT') {
      const raw = String(row.content ?? '').split('<ADDITIONAL_METADATA>')[0].replace(/^\s*<USER_REQUEST>\s*/, '').replace(/\s*<\/USER_REQUEST>\s*$/, '');
      const next = rows.slice(i + 1).find(r => r.type === 'PLANNER_RESPONSE');
      const limit = Math.min(8000, Math.max(16, options.truncatePrompt || 8000));
      events.push({ ...identity(conversation, row, 0, 'Prompt'), prompt_text: options.omitPrompt ? 'omit_by_config' : bounded(raw, limit), prompt_length: raw.length, 'gen_ai.request.model': modelName(next?.model || options.model) });
    } else if (row.type === 'PLANNER_RESPONSE') {
      (row.tool_calls ?? []).forEach((call, index) => calls.push({ ...call, row, index }));
      const cached = Number(row.cache_read_tokens) || 0;
      const fresh = Number(row.input_tokens) || 0;
      const output = Number(row.output_tokens) || 0;
      if (fresh || cached || output) events.push({ ...identity(conversation, row, 0, 'Completion'), 'gen_ai.request.model': modelName(row.model), 'gen_ai.usage.input_tokens': Number(row.prompt_tokens ?? row.total_input_tokens ?? fresh + cached), 'gen_ai.usage.cached_tokens': cached, 'gen_ai.usage.output_tokens': output });
    } else if (row.type === 'GENERIC') {
      const content = String(row.content ?? '');
      // Captured tool rows are MODEL rows with a Created At marker. Do not
      // consume queued calls for unrelated generic messages.
      if (row.source !== 'MODEL' || !/^Created At:/m.test(content)) continue;
      const task = /task id[:\s]+[\"]?([^\s\"]+)/i.exec(content)?.[1];
      if (task && expired.has(task)) continue;
      if (task && pending.has(task) && row.status === 'RUNNING') continue;
      if (task && pending.has(task)) { result(pending.get(task), content); pending.delete(task); expired.add(task); continue; }
      if (!calls.length) continue;
      const call = calls.shift();
      if (row.status === 'RUNNING' && task) pending.set(task, call);
      else { result(call, content); if (task) expired.add(task); }
    } else if (row.type === 'SYSTEM_MESSAGE') {
      const content = String(row.content ?? '');
      const task = /task id[:\s]+[\"]?([^\s\"]+)/i.exec(content)?.[1];
      if (task && pending.has(task)) { result(pending.get(task), content); pending.delete(task); expired.add(task); }
      else if (task && !expired.has(task) && calls.length && /finished with result/i.test(content)) { result(calls.shift(), content); expired.add(task); }
    }
  }
  for (const [task, call] of pending) {
    if (options.allowExpiry !== false && (options.now ?? Date.now()) - Date.parse(call.row.created_at) > 24 * 60 * 60 * 1000) { result(call, '[no completion observed]'); pending.delete(task); expired.add(task); }
  }
  if (options.state) { options.state.calls = calls; options.state.pending = Object.fromEntries(pending); options.state.expired = [...expired]; }
  return events;
}
