# Code reviews

This project provides GitHub and MCP code reviews and reviewer backtesting. Production reviews and quick single-PR tests share the execution engine. Dataset evaluations retain their own inputs and reports and do not publish GitHub comments or checks.

## Reviewer backtesting

In **Manage Agents → Test**, choose a named dataset and a saved reviewer revision or the current draft. **Run evaluation** saves the selected inputs and runs the reviewer against each PR. The quick single-PR test remains available and does not create a durable evaluation report.

**Manage datasets** opens a wide editor. Create a dataset name, then select PRs visually or enter PR numbers/URLs from the selected repository. Duplicate entries are included once. Invalid entries do not clear valid selections. Dataset edits and reference findings are staged together; **Save dataset** creates one new version.

Each PR preserves its captured patch and commit identity. Refreshing a PR is explicit and resets references for the changed input. Reference findings belong to the selected reviewer. **Unlabelled** means expected findings have not been reviewed; **Reviewed clean** explicitly means no findings are expected. These states are distinct.

The judge matches candidate findings to references and reports reference coverage, missed findings and additional **unverified** findings. An additional finding may be useful even when absent from the reference. Graded/finished indicates completion, not a passing verdict. Skipped, unlabelled and failed cases remain visible alongside the coverage counts.

**Add to reference** stages a finding in the dataset editor; it does not change the old run's grade. Only the dataset save commits a new reference version. Testing an old revision never restores or deploys the old agent configuration.

## Execution and storage

- One active evaluation per organization; two logical case pipelines, each reviewing then grading. Other organizations have independent capacity.
- Five logged, unpartitioned domain tables retain dataset/case state, reviewer revisions, runs and case results. Existing PR/review partitioning and retention are unchanged.
- Tenant-scoped immutable artifacts hold captured inputs and raw outputs. Report manifests copy references and effective configuration, not only hashes of mutable data.
- A failed-case retry creates a child report for that subset. A judge-failed retry reuses the persisted candidate output; successful cases and the original report are not overwritten.
- Lease ownership and database fencing prevent stale workers from committing results. Provider calls already in flight can outlive cancellation, so exactly-once billing is not guaranteed.

Reports show the requested Zeeq model tier and recorded provider/model identity for reviewer and judge. Internal credential references never appear in UI provenance. Admitted configuration and actual deployed runtime provenance are separate. Knowledge-base content remains live; backtesting does not replay an entire historical deployment or knowledge base.

## Rollout

`AppSettings:CodeReview:EvaluationsEnabled` (environment variable `AppSettings__CodeReview__EvaluationsEnabled`) defaults to `false`. Enable it only after the additive migration and evaluation consumer are ready. Disabling it stops new dataset actions/admissions while accepted runs can finish; quick single-PR testing and ordinary reviews remain available. Roll back by disabling admissions, cancelling or draining active runs, then reverting compatible web and worker binaries. Keep additive schema and artifacts.

## Verification

Backend tests live in `Zeeq.Platform.CodeReviews.Tests` and `Zeeq.Data.Postgres.Tests`; frontend view-model tests live beside the management components. Development fixtures use the existing 0factor personas and exact fixture repository IDs. They must not call real providers on a missing fixture or register in production. Keep fixture seed/cleanup explicit, and preserve unrelated users, roles and repositories.
