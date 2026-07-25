# Telemetry JSONB NUL Normalization Design

**Date**: 2026-07-25
**Status**: Approved

## Objective

Prevent a single telemetry event containing the NUL character (`U+0000`) from making the Postgres telemetry worker retry and fail an entire batch.

## Evidence

The production failure occurs in `PostgresAgentTelemetryDomainStore.InsertNewEventsAsync` when it serializes `AgentSessionEventInsertRow[]` and casts the parameter to `jsonb`. PostgreSQL rejects JSON strings containing `\\u0000` because its `text` representation cannot contain NUL bytes. The failed event batch includes an escaped NUL in tool telemetry.

## Options Considered

1. Sanitize only `output_snippet`.
   - Rejected: a NUL in prompt text or `arguments_json` would still poison the batch.
2. Replace escape text in the serialized JSON parameter.
   - Rejected: brittle and can corrupt legitimate escaped-backslash sequences.
3. Normalize typed data at the persistence boundary.
   - Selected: covers every stored event string and nested JSON string, is isolated to the Postgres implementation, and leaves accepted telemetry intact before persistence.

## Design

Update `src/backend/Zeeq.Data.Postgres/Telemetry/PostgresAgentTelemetryDomainStore.cs`:

- Add a private NUL-normalization helper with a no-allocation fast path for strings that do not contain `U+0000`.
- Apply it to all string-valued members when creating `AgentSessionEventInsertRow`.
- For `ArgumentsJson`, preserve the current cloned `JsonElement` if no NUL exists. If one does, recursively rebuild only JSON string values while retaining object keys, arrays, numbers, booleans, and nulls.
- Keep the JSON DTO and SQL projection unchanged; no migration is required.

## Performance

Clean values pay only an `IndexOf('\\0')` scan and no allocation. JSON is traversed/rebuilt only for the rare malformed payload. This is less expensive and more reliable than repeatedly retrying a failed batch.

## Validation

Extend `src/backend/Zeeq.Data.Postgres.Tests/TelemetryRawRequestStoreIntegrationTests.cs` with an integration test that:

1. Creates a claimed raw row and a tool-result event with NULs in `OutputSnippet` and nested `ArgumentsJson` values.
2. Calls `UpsertConversationsEventsAndAcknowledgeRawAsync`.
3. Verifies the event persists with NULs removed, nested JSON retains its shape, and the claimed raw row is deleted.

Run the focused TUnit test, then format and build the affected backend projects.

## Scope Limits

- No schema or migration changes.
- No mutation of inbound raw telemetry.
- No retry-policy changes.
