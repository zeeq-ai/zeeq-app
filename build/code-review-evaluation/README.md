# Reviewer backtesting fixture helper

Development-only fixture data for reviewer backtesting acceptance (BT-01..BT-07).
It never runs at application startup and refuses to run outside a local Development database.

## What it creates

Everything is namespaced with `bt_{runId}`:

| Item | Alpha | Beta |
| --- | --- | --- |
| Repository | `repo_bt_{runId}_alpha` | `repo_bt_{runId}_beta` |
| Reviewers | `cra_bt_{runId}_alpha_correctness`, `..._alpha_security` | `cra_bt_{runId}_beta_*` |
| Revisions | `crr_bt_{runId}_alpha_*` | `crr_bt_{runId}_beta_*` |
| Dataset | `cred_bt_{runId}_alpha` | none |
| Capture-only dataset | `cred_bt_{runId}_capture` (archived) | none |
| Pull request rows | `#901`-`#910` with lookups | `#901`-`#910` with lookups |

The Alpha dataset holds eight cases, `crec_bt_{runId}_901` through `..._908`, with immutable
snapshot artifacts under `reviewer-evaluations/{org}/{repo}/snap_bt_{runId}_*/snapshot.json`.

Fixture intent:

- P1 `#901`: reviewer content `Expected` with `R1` (exact-match ranking) and `R2` (wildcard
  escaping); the deterministic fake reviewer returns `R1` plus one additional finding.
- P2 `#902`: `ReviewedClean`, valid empty candidate.
- P3 `#903`: `Unlabelled`; output is retained but ungraded.
- P4 `#904`: changed file is `docs/README.md` and the repository filter excludes `docs/`, so the
  deterministic outcome is `NoFilesInScope`.
- P5 `#905`: changed file is `src/activation/ActivationOnly.cs` and only the correctness reviewer
  excludes `src/activation/` by activation rule, so the outcome is `NoAgentActivation`.
- P6 `#906`: reviewer throws; expected `ReviewFailed`.
- P7 `#907`: candidate persists, judge returns invalid JSON; expected `JudgeFailed`.
- P8 `#908`: extra eligible case for the two-pipeline barrier proof.
- `#909` and `#910`: browsable pull requests with seeded snapshots in the archived capture
  dataset. They are **not** members of the curated dataset, so Add PRs can resolve and capture
  them while dataset membership stays exactly P1-P8.

The Beta repository has no dataset: it exists to prove per-organization admission independence
and tenant boundaries. A route organization that differs from the active cookie organization
returns 403 through the existing route guard. Missing or cross-repository parents within the
active authorized organization return 404.

## Run it

From the repository root, with the local Aspire stack running (Postgres on `35432`):

```sh
DOTNET_ENVIRONMENT=Development \
BT_RUN_ID=local1 \
ConnectionStrings__zeeq='Host=localhost;Port=35432;Database=zeeq;Username=zeeq;Password=P@ssw0rd' \
dotnet run build/code-review-evaluation/seed.cs
```

`ConnectionStrings__zeeq-db` is also accepted when `ConnectionStrings__zeeq` is absent. The
helper rejects a non-Development environment, a non-loopback host, or a database other than
`zeeq`.

The 0factor baseline (`build/0factor/seed.cs`) must exist first: the helper expects
`org_0factor_e2e_alpha` and `org_0factor_e2e_beta` and fails loudly when they are missing.

## Development-only source registration

The host owns the single allowlist contract
(`Zeeq.Platform.CodeReviews.CodeReviewEvaluationFixtureRepositories`, constructed with the
environment and the exact organization/repository pairs from the manifest) and registers it in
Development. It then calls the no-argument extension for the source decorators only:

```csharp
services.AddZeeqCodeReviewEvaluationFixtures();
```

That adds two decorators: the evaluation capture source used by Resolve/Refresh, and the
production pull request source used by the quick-test path. There is no second allowlist. Both serve seeded snapshots for allowlisted repositories, delegate to the
real provider for everything else, and fail closed with `pr_not_found` when a fixture pull request
has no seeded snapshot. Registration must never happen in Production.

## Manifest

The seed writes `build/code-review-evaluation/bt_{runId}.manifest.json` with every repository,
reviewer, revision, dataset, case, and snapshot artifact URI it created.

A second seed with the same `BT_RUN_ID` refuses to run while that manifest exists, so two runs
can never mix data. Use a new `BT_RUN_ID` for a fresh scenario.

## Clean up

```sh
DOTNET_ENVIRONMENT=Development \
BT_RUN_ID=local1 \
BT_MODE=cleanup \
ConnectionStrings__zeeq='Host=localhost;Port=35432;Database=zeeq;Username=zeeq;Password=P@ssw0rd' \
dotnet run build/code-review-evaluation/seed.cs
```

Cleanup deletes exactly what the manifest owns, in dependency order: evaluation case results,
runs discovered through the manifest datasets, cases, datasets, revisions, reviewers,
repositories, and artifacts. Because runs created during browser acceptance are not known at
seed time, it also removes candidate output artifacts under
`reviewer-evaluations/{org}/{repo}/` for fixture-owned repositories only. It then verifies no
dataset remains and deletes the manifest.

Cleanup refuses to run when the manifest is missing; it never guesses what to delete. It does
not reset the database, delete provider volumes, or stop the stack.
