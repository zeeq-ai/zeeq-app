# PR Session Link Read-Before-Insert Design

**Date**: 2026-07-30

## Objective

Avoid routine error-level EF Core logs when an existing pull-request-to-conversation
link is encountered, while preserving database-enforced correctness under concurrent
insert races.

## Design

`PostgresAgentTelemetryDomainStore.TryCreatePullRequestSessionLinkAsync` will first
query for the natural key:

- `OrganizationId`
- `PullRequestRecordId`
- `ConversationId`

If the link exists, the method returns `false` without tracking or inserting the
candidate entity. If it does not exist, the existing EF Core `Add` and
`SaveChangesAsync` path remains unchanged. The filtered PostgreSQL unique-constraint
catch remains in place because two workers can both complete the read before either
inserts.

The existence query will be no-tracking and tagged with an operation call site.
No model, migration, interface, or caller changes are required.

## Error Handling

Only the existing natural-key unique violation is converted to `false`. All other
database errors continue to propagate. A losing concurrent insert is detached from
the DbContext exactly as it is today.

## Verification

- Add a Postgres integration test for a sequential duplicate.
- Retain and run the existing concurrent duplicate test.
- Run formatting and relevant diagnostics.
- Run Zeeq expert code review on the completed diff.
