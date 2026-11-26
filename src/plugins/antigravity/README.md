# Zeeq telemetry for Antigravity

The plugin sends session prompts, tool calls and results, and model token usage to Zeeq. It reads the Antigravity transcript after each turn. Hooks fail open if telemetry cannot be sent.

## Install

```sh
agy plugin install https://github.com/zeeq-ai/zeeq-app/tree/main/src/plugins/antigravity
```

For a local checkout, use `agy plugin install /path/to/zeeq-app/src/plugins/antigravity`. Pin the URL to a release tag when one is available. Node.js 20 or later is required.

Set `ZEEQ_ACCESS_TOKEN` to a Zeeq user token in the environment that starts Antigravity. A user token associates the session with its owner. For IDE launches without shell environment variables, write the token as plain text to `~/.config/zeeq/credentials` and set its permissions to `0600`. Environment variables take precedence. `LOCAL_ZEEQ_ACCESS_TOKEN` is a fallback for local testing.

`ZEEQ_BASE_URL` defaults to `https://app.zeeq.ai`. Set it when using another deployment. `ANTIGRAVITY_TELEMETRY_ENDPOINT` overrides the full import URL.

## Privacy

Prompts and tool arguments and results can contain sensitive information. They are sent to Zeeq. Set `ZEEQ_OTEL_OMIT_PROMPT=1` to replace prompt content with `omit_by_config`, or set `ZEEQ_OTEL_TRUNCATE_PROMPT` to a character limit of at least 16. Prompt content is limited to 8,000 characters. Tool arguments and results are limited to 4,000 characters. Tool results can contain personal data.

## Troubleshooting

Set `ANTIGRAVITY_TELEMETRY_DISABLED=1` to disable export. Set `ANTIGRAVITY_TELEMETRY_LOG_ERRORS=1` for error logging. The plugin writes its latest delivery error to `lastError` in its Antigravity plugin data directory. `ANTIGRAVITY_TELEMETRY_TIMEOUT_MS` changes the request timeout (default 3000). A later session retries unsent data from inactive conversations. A final interrupted session may remain unsent until another session starts.

Antigravity's transcript format is not a documented stable API. IDE and interactive launch behavior may differ from CLI operation.

Tool results are paired by transcript order because Antigravity does not provide tool-call IDs. Background results are matched by task ID. An unresolved background task is reported as having no observed completion after 24 hours; a later result cannot replace it. The plugin recognizes the captured CLI tool-result format, which may change with Antigravity versions.

When a batch is rejected, the plugin retains its checkpoint. After three rejected attempts at the same cursor, it checks up to 200 events individually per run. If some events are accepted, rejected events are dropped with a diagnostic. If every event is rejected, the plugin retains the batch and suspends delivery for 24 hours or until the plugin version changes. Malformed complete transcript rows are skipped with a diagnostic.
