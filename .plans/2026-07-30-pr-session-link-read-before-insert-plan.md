# PR Session Link Read-Before-Insert Implementation Plan

## Objective

Prevent routine duplicate PR-session associations from reaching `SaveChangesAsync`
while retaining the database unique constraint and filtered exception handling as the
concurrency backstop.

## Gotchas to Avoid

- A read check does not provide concurrency safety; retain the unique constraint catch.
- Use all three natural-key columns, including `OrganizationId`.
- Use `AsNoTracking` and an operation call-site tag for the existence query.
- Detach the attempted insert after a raced unique violation so the DbContext remains usable.
- Do not touch the unrelated work currently present in the shared worktree.

## Patterns Applied

- EF Core no-tracking existence query for read-only checks.
- Database unique constraint remains the authoritative concurrency decision.
- TUnit Postgres integration coverage through `PgDatabaseFixture`.

## Validation Methodology

**Primary validation:** Run the Postgres telemetry store integration-test class.

**Expected result:** The sequential duplicate and concurrent duplicate tests both pass;
all other telemetry store integration tests remain green.

**Validation command:**

```bash
dotnet run --project src/backend/Zeeq.Data.Postgres.Tests/Zeeq.Data.Postgres.Tests.csproj -- --treenode-filter "/*/*/TelemetryRawRequestStoreIntegrationTests/*" --output detailed --disable-logo
```

**Why this validates the feature:** It exercises the real EF Core mapping and PostgreSQL
unique index, including both the common sequential path and the remaining concurrency
race.

**Testability considerations:** The sequential test uses two store calls with the same
natural key. The existing concurrent test continues to prove that the catch path works.

## PR Stack

| PR | Branch | Steps | Description |
|----|--------|-------|-------------|
| 1 | fix/pr-session-link-duplicate-logs | 1-3 | Add the existence check, integration coverage, and validation |

## Implementation Steps

### Step 1: Add the Read-Before-Insert Check

**File:**

- `src/backend/Zeeq.Data.Postgres/Telemetry/PostgresAgentTelemetryDomainStore.cs`

**Changes:**

- Query the link set for the full natural key before tracking the candidate entity.
- Return `false` when the durable link already exists.
- Retain the existing insert and filtered `DbUpdateException` catch unchanged.

**Verification:**

- [ ] Glider diagnostics report no new errors.
- [ ] CSharpier formatting succeeds.

### Step 2: Add Sequential Duplicate Coverage

**File:**

- `src/backend/Zeeq.Data.Postgres.Tests/TelemetryRawRequestStoreIntegrationTests.cs`

**Changes:**

- Add an integration test that inserts a link, attempts the same natural key again with
  a different row ID, and verifies `true` followed by `false`.
- Verify only one durable row exists for the natural key.

**Verification:**

- [ ] Sequential duplicate test passes.
- [ ] Existing concurrent duplicate test passes.

### Step 3: Validate and Review

**Files:**

- The two modified C# files.

**Changes:**

- Run the telemetry store integration-test class.
- Run Zeeq expert code review on the scoped diff and address actionable findings.

**Verification:**

- [ ] Primary validation command passes.
- [ ] Expert review has no unresolved correctness findings.

## Key Decisions

- **Duplicate handling:** Use read-first plus the existing catch, as selected by the user.
- **Storage abstraction:** Keep the change in the Postgres store without changing its interface.
- **Schema:** Retain the existing unique index; no migration is needed.
