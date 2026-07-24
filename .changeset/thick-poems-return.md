---
"@zeeq-ai/zeeq-app-pi-telemetry": minor
---

Realign the extension with Zeeq's actual telemetry import contract (`/api/v1/telemetry/import`, `Prompt`/`ToolResult`/`Completion` event kinds, request-level conversation/repository fields) instead of the stale Biblio-era shape. Adds local git remote/branch/SHA collection for PR-provenance linking. Renames env vars to `ZEEQ_ACCESS_TOKEN`/`ZEEQ_BASE_URL`/`LOCAL_ZEEQ_ACCESS_TOKEN` and drops `PI_TELEMETRY_USER_EMAIL`, since the server now stamps `owner_email` from the authenticated bearer token instead of trusting a client-reported value.
