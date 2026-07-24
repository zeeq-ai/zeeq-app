# Zeeq App Pi Telemetry

This package installs the Pi lifecycle telemetry extension for Zeeq App. It subscribes to Pi session, prompt, agent, model, thinking-level, and tool lifecycle events, then posts normalized telemetry to Zeeq's REST import endpoint.

## Install From This Repository's GitHub Package

This package is published to GitHub Packages from the `zeeq-ai/zeeq-app` repository as:

```text
@zeeq-ai/zeeq-app-pi-telemetry
```

Configure npm to read Zeeq packages from GitHub Packages:

```bash
npm config set @zeeq-ai:registry https://npm.pkg.github.com
npm config set //npm.pkg.github.com/:_authToken "$(gh auth token)"
```

### Local Project Install (Preferred)

Install the Pi package project-locally from the repository root that should emit telemetry:

```bash
# Local default: installs the package using npm's default latest version.
pi install --local npm:@zeeq-ai/zeeq-app-pi-telemetry
```

To pin a published version project-locally:

```bash
pi install --local npm:@zeeq-ai/zeeq-app-pi-telemetry@0.1.0
```

Pi writes the package source to `.pi/settings.json`:

```json
{
  "packages": [
    "npm:@zeeq-ai/zeeq-app-pi-telemetry@0.1.0"
  ]
}
```

Use the unpinned local install when each repository can accept npm's current `latest` package version. Use the pinned local install when shared project settings should keep every developer on the same extension code.

Run Pi with project approval so it trusts and loads the project-local package:

```bash
pi --approve
```

For non-interactive checks, pass `--approve` to the command:

```bash
pi --approve -p "exit"
```

### Global Install (Opt In)

Use global installation only when this extension should load for every Pi session in the user's environment:

```bash
pi install npm:@zeeq-ai/zeeq-app-pi-telemetry
```

Project-local installation is preferred for this extension because telemetry ownership, source naming, and package versioning should be controlled by the repository that opts in.

Global installation lets each user decide to enable Zeeq telemetry across their own Pi environment. It does not write package settings into the repository.

Reference: <https://pi.dev/docs/latest/packages#install-and-manage>

## Runtime Configuration

Set the bearer token before starting Pi:

```bash
export ZEEQ_ACCESS_TOKEN="..."
```

For the hosted default, this is the only required runtime setting.

By default, the extension sends telemetry to:

```txt
https://app.zeeq.ai/api/v1/telemetry/import
```

Use `ZEEQ_BASE_URL` when your team runs Zeeq on a custom URL or domain:

```bash
export ZEEQ_BASE_URL="https://zeeq.example.com"
```

Use `PI_TELEMETRY_ENDPOINT` only when you need to override the full import endpoint, including the path:

```bash
export PI_TELEMETRY_ENDPOINT="https://zeeq.example.com/api/v1/telemetry/import"
```

For local testing against Aspire, the API is reachable through the local YARP proxy without the `/web` base path, e.g. `http://zeeq-api.localhost:8095`.

`ZEEQ_ACCESS_TOKEN` is the preferred token name and is sent as the `Authorization: Bearer ...` header. `LOCAL_ZEEQ_ACCESS_TOKEN` is still supported as a local-development fallback, and `PI_TELEMETRY_API_KEY` is retained as a legacy fallback.

Optional settings:

```bash
export ZEEQ_BASE_URL="https://app.zeeq.ai"
export PI_TELEMETRY_ENDPOINT="https://app.zeeq.ai/api/v1/telemetry/import"
export LOCAL_ZEEQ_ACCESS_TOKEN="..."
export PI_TELEMETRY_API_KEY="..."
export PI_TELEMETRY_BATCH_SIZE="10"
export PI_TELEMETRY_FLUSH_MS="5000"
export PI_TELEMETRY_TIMEOUT_MS="3000"
export PI_TELEMETRY_RETRY_MS="30000"
export PI_TELEMETRY_LOG_ERRORS="true"
export PI_TELEMETRY_DISABLED="true"
```

`ZEEQ_BASE_URL` can be a full URL or a bare domain. Bare domains are treated as HTTPS. `PI_TELEMETRY_ENDPOINT` takes precedence over `ZEEQ_BASE_URL`.

There is no user-email setting — `AgentTelemetryImportHandler` always stamps `owner_email` from the bearer token's own `email` claim server-side and ignores anything the client reports, so there is nothing for the extension to configure here. Ownership (`CreatedById`) comes from the same token identity.

The extension reads the local git remote, branch, and commit once at session start (`git config --get remote.origin.url`, `git rev-parse --abbrev-ref HEAD`, `git rev-parse HEAD`) and sends them as `repository_remote_url` / `head_branch` / `head_sha` on every request so Zeeq can link the conversation to a pull request. Any of these can be `null` when the command fails (e.g. not a git repository).

## Usage, Cost, And Model Names

The extension exports Pi lifecycle events as one of the three event kinds Zeeq's import contract accepts:

- `input` -> `Prompt`
- `tool_result` / `tool_execution_end` -> `ToolResult`
- `agent_end` -> `Completion`

There is no session-start or end-of-session-summary event kind on the current contract — conversation identity (`conversation_id`, `harness_name`, `repository_remote_url`, `head_branch`, `head_sha`) is carried at the request level instead, and is attached to every flush.

Pi stores provider usage on final assistant messages. When `message.usage` is present, the extension sends token counts. When `message.usage.cost.total` is present and nonzero, the extension sends `zeeq.cost.usd` so Zeeq can persist the provider-normalized cost instead of estimating it later.

Provider error responses can include a usage object with all counts set to zero. The extension drops those completion events, because a zero-token provider error is not a billable completion.

Pi model IDs are normalized before export. Provider-qualified IDs such as `openai-codex/gpt-5.5` and `anthropic/claude-haiku-4-5` are exported as `gpt-5.5` and `claude-haiku-4-5`. This keeps imported rows aligned with Zeeq's pricing catalog keys.

Zeeq's pricing catalog is maintained in `src/backend/Zeeq.Platform.Telemetry/Processing/AgentTelemetryCostEnricher.cs`. When `zeeq.cost.usd` is present, Zeeq treats it as authoritative for the completion total. When it is absent, `AgentTelemetryCostEnricher` estimates cost from token counts and that catalog. Check the enricher for the current model list and rates — it changes independently of this extension.

Pricing changes over time. Before relying on estimated cost for a new model, check the current provider pricing references and the catalog's `Version` in `AgentTelemetryCostEnricher.cs`:

- OpenAI API pricing: <https://openai.com/api/pricing/>
- Anthropic Claude API pricing: <https://docs.anthropic.com/en/docs/about-claude/pricing>

## Versioning And Publish

Versioning is managed by [Changesets](https://github.com/changesets/changesets), not hand-picked version numbers or manually pushed tags. This keeps the committed `package.json` version, the changelog, and the published npm version from drifting out of sync with each other.

### Contributing a change

If your PR changes `src/plugins/pi`, add a changeset describing it:

```bash
yarn changeset
```

Select `@zeeq-ai/zeeq-app-pi-telemetry`, pick a bump type (patch/minor/major), and write a one-line summary. Commit the generated `.changeset/*.md` file with the rest of your PR — it becomes the changelog entry.

Skip this for changes that don't affect the published package (README-only edits, workflow tweaks, etc.).

### How publishing happens

Publishing is handled by `.github/workflows/publish-pi-package.yml`, driven by [`changesets/action`](https://github.com/changesets/action). It runs on every push to `main` (and manual `workflow_dispatch` as an escape hatch to re-run after a registry or workflow failure):

1. If any changesets are pending, the workflow opens (or updates) a **"Version Packages"** PR that bumps `package.json` and the changelog. Nothing is published yet.
2. Merging that PR removes the pending changesets. The next run of the workflow finds none pending, so it publishes instead: `npm publish` runs from `src/plugins/pi` (honoring its own `publishConfig`), and a git tag is pushed in Changesets' default `<package-name>@<version>` format, e.g. `@zeeq-ai/zeeq-app-pi-telemetry@0.2.0`.

There is no manual version-editing or tagging step — merging the bot's "Version Packages" PR is the entire release action.

## Pi Package Metadata

The package uses Pi's `package.json` manifest support:

```json
{
  "pi": {
    "extensions": ["./src/index.ts"]
  }
}
```

References:

- Pi package creation: <https://pi.dev/docs/latest/packages#creating-a-pi-package>
- Pi package dependencies: <https://pi.dev/docs/latest/packages#dependencies>
- Extension locations and trust: <https://pi.dev/docs/latest/extensions#extension-locations>
- Lifecycle overview: <https://pi.dev/docs/latest/extensions#lifecycle-overview>
- Tool events: <https://pi.dev/docs/latest/extensions#tool-events>

## Local Development Testing

Load the extension directly with `-e`, no install step needed:

```bash
pi -e src/plugins/pi/src/index.ts --approve
```

Point it at the local Aspire stack instead of the hosted default:

```bash
export ZEEQ_BASE_URL="http://zeeq-api.localhost:8095"
export LOCAL_ZEEQ_ACCESS_TOKEN="..."
```

If you have a production token, you'll need to unset it in the test terminal — `ZEEQ_ACCESS_TOKEN` takes precedence over `LOCAL_ZEEQ_ACCESS_TOKEN`, so a token minted against the hosted Zeeq issuer silently 401s against your local one:

```bash
unset ZEEQ_ACCESS_TOKEN
```

Verify delivery with `aspire logs zeeq-server` and `aspire logs yarp-reverse-proxy`, or query `zeeq.auth_user_tokens` / `agent_session_events` directly with `psql`. A 401 with no application log line means auth rejected the token before it reached the handler — check the token's `iss`/`aud`/`sub` claims first.

## Zeeq Ingest Sources

- Route mapping: `src/backend/Zeeq.Platform.Telemetry/Ingest/OtlpIngestEndpoints.cs`
- Import handler: `src/backend/Zeeq.Platform.Telemetry/Ingest/Import/AgentTelemetryImportHandler.cs`
- Import models: `src/backend/Zeeq.Platform.Telemetry/Ingest/Import/AgentTelemetryImportModels.cs`
- Import validation: `src/backend/Zeeq.Platform.Telemetry/Ingest/Import/AgentTelemetryImportValidator.cs`
- OTLP mapping: `src/backend/Zeeq.Platform.Telemetry/Ingest/Import/AgentTelemetryImportOtlpMapper.cs`
- JSON-import adapter (normalizes into domain rows): `src/backend/Zeeq.Platform.Telemetry/Adapters/ZeeqAgent/ZeeqAgentTelemetryAdapter.cs`
- Cost enrichment: `src/backend/Zeeq.Platform.Telemetry/Processing/AgentTelemetryCostEnricher.cs`
- Async processing loop: `src/backend/Zeeq.Platform.Telemetry/Processing/TelemetryProcessingService.cs`
