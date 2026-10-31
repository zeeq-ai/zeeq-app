# Claude Code telemetry

## Launch configuration

Claude Code ignores telemetry enablement and exporter variables in repository `.claude/settings.json` and `.claude/settings.local.json`. This repository supplies them from `.config/mise.toml` before Claude starts. It enables telemetry only when your private environment already has a non-empty `ZEEQ_OTEL_ACCESS_TOKEN`.

```sh
mise exec -- claude
```

The checked-in `otelHeadersHelper` remains a Claude setting. Its script reads the inherited token and returns an Authorization header. Do not copy the token into this repository or print the script output. HTTP/protobuf supports this dynamic header helper. MCP login credentials are separate and do not automatically configure telemetry authentication.

The default collector is `https://otel.zeeq.ai`. It receives session logs, which Zeeq uses to derive token and cost metrics; native OTLP metrics export is disabled to avoid a second accounting path. Prompt text and MCP tool details use the repository's existing capture settings. Set the capture flags to `0` in a private mise override if you do not want that content exported. Restart existing Claude sessions after configuration changes. Remove stale telemetry `env` entries from `.claude/settings.local.json`; leave unrelated settings intact.

For a local test, override the collector endpoint in a private mise environment and use a token issued by local Zeeq. A production token cannot authenticate to the local collector. The local collector already forwards accepted logs to both Aspire and Zeeq. Do not disable issuer validation to make a production token work locally.

References: [Claude telemetry configuration](https://code.claude.com/docs/en/monitoring-usage#administrator-configuration), [dynamic headers](https://code.claude.com/docs/en/monitoring-usage#dynamic-headers), [mise environments](https://mise.jdx.dev/environments/).

## Work, internal requests, and billing

`query_source` on Claude event logs names the originating subsystem. It is not the `main` / `subagent` / `auxiliary` category used on Claude's native metrics. SDK, interactive, subagent, missing, and unknown event sources can represent real work. Zeeq marks only the verified `generate_session_title` source as housekeeping.

Housekeeping prompts and tool activity do not contribute to activity counts. All accepted completion tokens and reported costs contribute to billing metrics, including internal requests, because they incur real charges. Aggregate token charts count input plus output once; cached and reasoning token samples describe subsets and must not be added again. Legacy samples without a token-kind label remain included. Event insertion deduplicates repeated exports before live metrics are emitted; this does not guarantee delivery after a process crash in the existing loss-tolerant metrics path.

Claude conversations appear in the Sessions inbox only when their persisted history contains a non-housekeeping prompt, tool result, or completion. Internal-only, decision-only, and metadata-only conversations remain available by direct detail link. A later work event can make a session visible. The separate minimum-cost filter still applies. Other harnesses keep their existing visibility rules.

## Historical SDK classification repair

Deploy the corrected classifier before considering a repair. Existing SDK events can still have `is_housekeeping=true`. Their stored token/cost fields and conversation billing totals already include their completion usage; those amounts must not be rewritten or re-emitted.

The adjacent psql script repairs only exact `claude-code` / `query_source='sdk'` / old flag `true` rows. It requires one organization and a half-open UTC interval of at most 30 days. It aborts above 10,000 candidates; narrow the interval rather than treating a partial repair as complete. No-content JSONL output identifies each composite event key and retained billing fields. Keep that output in private operational evidence, not Git.

Preview, using your normal database connection environment:

```sh
psql -X -qAt -v ON_ERROR_STOP=1 \
  -v organization_id=org_example \
  -v from_utc=2026-10-01T00:00:00Z \
  -v to_utc=2026-10-31T00:00:00Z \
  -f build/telemetry/repair-claude-sdk-classification.sql > sdk-preview.jsonl
```

After reviewing the manifest and target, run the same command with `-v apply=true` and save a separate applied manifest. The script changes only `is_housekeeping`, in one bounded transaction with lock and statement timeouts. Check the process exit status: output before a failed commit is not proof of a committed repair. A repeat apply must report zero changes. Other harnesses, title-generation events, and billing values remain unchanged. Prompt-derived titles may need a separate explicit repair.

This repair does **not** reconstruct historical metric charts. Existing metric samples have no event/session identity and use capture time rather than event time. They also have loss-tolerant delivery and finite retention. Replaying ingestion or live metric emission can double count usage. A historical metric rebuild needs a separate reviewed provenance and repair-ledger design; this script does not insert metric samples or restore expired metric partitions.
