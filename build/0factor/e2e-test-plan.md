# 0factor authentication end-to-end test plan

Reusable instructions for checking the local Zeeq and 0factor integration. This plan covers the local identity contract and repeatable automated and manual checks.

## Check types

- **Automated:** TUnit checks in the backend test projects.
- **Live protocol tests:** Explicit `ZeroFactorProtocolIntegrationTests` in the Identity test project check the running 0factor OIDC boundary.
- **Manual browser/API:** Interactive checks using an authorized Paseo browser signed in as one of the synthetic 0factor personas.
- **Unsupported:** The app has no API to select a custom team as the active team and no team-scoped authorization operation for this matrix. Verify persisted team memberships and active root-team context only.

Never place cookies, authorization codes, state values, PKCE verifiers, access or refresh tokens, client secrets, or full redirect URLs in logs or this plan. Keep captured results to non-secret status and identity/context fields.

## Prerequisites and fixture setup

1. Run the local Aspire AppHost. Wait for `mock-oidc`, `zeeq-server`, and the web app to become healthy. The provider realm is `zeeq`, issuer `http://localhost:9321/realms/zeeq`, public client `mcp-openiddict-local`, and stable Zeeq provider key `mock` (display label `0factor`).
2. Confirm the imported OIDC client uses authorization code with PKCE S256, `lenient:false`, and exactly these callbacks:
   - `http://zeeq-web.localhost:8095/auth/callback/mock`
   - `http://localhost:8096/auth/callback/mock`
3. Start the fixture resource explicitly from the repository root:

        aspire resource auth-e2e-fixtures start --apphost ./host --non-interactive --nologo

   The one-file app is also runnable with `dotnet run --file build/0factor/seed.cs`. It requires `DOTNET_ENVIRONMENT=Development` and `ConnectionStrings__zeeq` for the local `zeeq` database. The app rejects non-Development environments, non-loopback PostgreSQL hosts, and a database name other than `zeeq`. It never prints the connection string.
4. The seed uses fixed IDs and a transaction. It upserts four local identities, two organizations, four teams, active organization/team membership rows, and resets only the Dana invitation to pending. An ownership mismatch aborts the transaction. It does not create Dana's local identity, alter the legacy `mock/johndoe` identity, or delete rows outside its fixture.
5. Use the authorized Paseo browser for persona sign-ins. 0factor's browser session is shared across apps in the realm; to change personas, sign out of Zeeq and clear the realm session at `http://localhost:9321/realms/zeeq/protocol/openid-connect/logout` before starting the next sign-in.
6. To repeat Dana's first-login path, use a local database that has no `(mock, e2e-dana-invitee)` identity. The seed can restore the pending invitation after acceptance, but it deliberately preserves Dana's local identity and related onboarding rows.

The provider's named Docker volume is `zeeq-0factor-data`. Do not remove it as routine test cleanup; it stores signing keys and any manually created development users.

## Stable personas and local fixture

The realm import is `build/0factor/realms/zeeq-realm.json`; `users[].id` is the OIDC `sub`. Zeeq identity continuity is keyed by `(provider, provider_subject)`, not by issuer.

| Persona | Subject / email | Organization memberships | Team memberships |
| --- | --- | --- | --- |
| Alice | `e2e-alice-owner` / `alice@e2e.example.test` | Alpha owner; Beta member | Alpha root owner; Engineering admin |
| Bob | `e2e-bob-multi` / `bob@e2e.example.test` | Alpha member; Beta admin | Alpha root member and Engineering member; Beta root admin |
| Cara | `e2e-cara-research` / `cara@e2e.example.test` | Alpha member only | Alpha root member and Research member |
| Dana | `e2e-dana-invitee` / `dana@e2e.example.test` | Pending Alpha member invitation until acceptance | No team membership before invite acceptance |
| Erin | `e2e-erin-outsider` / `erin@e2e.example.test` | Beta member only | Beta root member |
| Legacy Mock User | `johndoe` / `johndoe@e2e.example.test` | Existing database state; fixture does not change it | Existing database state; fixture does not change it |

The fixture organizations are active: `org_0factor_e2e_alpha` and `org_0factor_e2e_beta`. Their teams are `team_0factor_e2e_alpha_root`, `team_0factor_e2e_alpha_engineering`, `team_0factor_e2e_alpha_research`, and `team_0factor_e2e_beta_root`. Alice, Bob, Cara, and Erin have fixed local Zeeq user IDs. Dana receives a local identity through the normal first-login invitation flow.

Organization roles and team roles are separate persisted memberships. `POST /api/v1/me/switch-org/{orgId}` changes the active organization and resets active team context to that organization's root team. Do not infer custom-team authorization from a stored team membership.

## Automated checks

Run the Identity and Membership projects:

    dotnet run --project src/backend/Zeeq.Core.Identity.Tests --output detailed --disable-logo
    dotnet run --project src/backend/Zeeq.Platform.Membership.Tests --output detailed --disable-logo

Run the provider catalog checks and legacy identity-continuity regression directly:

    dotnet run --project src/backend/Zeeq.Core.Identity.Tests --output detailed --disable-logo --treenode-filter "/*/*/ProviderAuthCatalogTests/*"
    dotnet run --project src/backend/Zeeq.Core.Identity.Tests --output detailed --disable-logo --treenode-filter "/*/*/PostgresZeeqIdentityStoreTests/*ExistingLegacyMockIdentity*"

The catalog tests assert the development provider values and verify `includeMock:false` removes the mock provider. The Postgres regression builds a persisted owner, organization, root team, and memberships using `TestBiome`, links the pre-existing `mock/johndoe` identity to that user, then checks that a single subsequent login returns those same IDs without duplicates.

With the local stack healthy, run the explicit login-start test:

    dotnet run --project src/backend/Zeeq.Core.Identity.Tests --output detailed --disable-logo --treenode-filter "/*/*/ExternalLogin0FactorIntegrationTests/*"

It validates the public login redirect against the exact callback allow-list, realm client, authorization-code response type, OIDC scopes, state, and S256 challenge. It does not follow the redirect or print state/challenge values.

Run the explicit live provider protocol tests:

    aspire wait mock-oidc --apphost ./host --timeout 60 --non-interactive
    dotnet run --project src/backend/Zeeq.Core.Identity.Tests -- --output detailed --disable-logo --treenode-filter "/*/*/ZeroFactorProtocolIntegrationTests/*"

These C#/TUnit tests check discovery, signing keys, all imported personas, both callback values, PKCE code exchange, userinfo, and negative protocol cases. The checked-in realm JSON is copied to the test output so the persona list stays aligned with the import. The tests use .NET cryptography without Python or OpenSSL command-line tools. They do not verify Zeeq cookies, org membership, app API authorization, or MCP setup ownership. Ordinary test runs do not contact the live provider; select this class explicitly after starting Aspire.

Existing tests also cover token issuer/audience validation, safe return URLs, auth-state purpose/expiry/one-time consumption, membership switching, invitation ownership, and organization activation filtering.

## Browser and API matrix

Use same-origin requests in the authorized Paseo browser. Record response status and only the user and organization fields needed for each assertion.

| ID | Persona and action | Setup, action, and assertions | Check type |
| --- | --- | --- | --- |
| E1 | Alice seeded-account sign-in | Use the fixture's existing Alice account for her first 0factor sign-in. `GET /api/v1/me`; confirm stable local Alice ID, provider `mock`, subject `e2e-alice-owner`, active Alpha, role `owner`, and Alpha root team. This checks external identity resolution for a pre-existing Zeeq account, not new-account bootstrap. | Manual browser/API; persistence regression automated separately |
| E2 | Bob's two-organization roles | Sign in as Bob and inspect `/api/v1/me`; confirm Alpha `member` and Beta `admin`. Switch to Alpha with `POST /api/v1/me/switch-org/org_0factor_e2e_alpha`, then confirm Alpha `member` and Alpha root. Switch to Beta and confirm Beta `admin` and Beta root. | Manual browser/API |
| E3 | Cross-organization denial | While Erin is active in Beta, request `GET /api/v1/orgs/org_0factor_e2e_alpha/me/aliases`; expect `403`. Try switching to Alpha; expect `404`. Confirm `/api/v1/me` remains in Beta and does not list Alpha. | Manual browser/API |
| E4 | Cara's team memberships | Sign in as Cara; confirm Alpha only and active Alpha root in `/api/v1/me`. Inspect fixture rows and verify Cara has root and Research memberships, no Engineering membership, and no Beta membership. No custom-team selection or team-scoped authorization assertion is supported. | Manual browser/API and read-only database inspection; custom-team authorization unsupported |
| E5 | Dana first-login invitation | Begin with Dana's pending Alpha invitation and no local Dana identity. Sign in as Dana. Confirm `GET /api/v1/me/invitations` returns that invitation. Before acceptance, `/api/v1/me` redirects to login because no active organization is selected; a redirect-following fetch may show the login page with HTTP 200. Accept with `POST /api/v1/me/invitations/{inviteId}/accept` and expect `204`; switch to Alpha and expect `204`; refetch `/api/v1/me` and confirm Dana's new local user, Alpha `member`, Alpha root, and no pending invitation. | Manual browser/API |
| E6 | Invitation email ownership gate | Before E5 acceptance, sign in as Erin and try to accept Dana's invitation. Expect `404`, and confirm the invitation remains pending for Dana. | Manual browser/API; handler/store ownership checks automated |
| E7 | Alice logout and identity continuity | Sign in as Alice; record the local User ID and Alpha context. `POST /auth/logout` should return `204`; `/api/v1/me` should then be unauthorized. Clear the 0factor realm session, sign in as Alice again, and confirm the same local User ID and Alpha owner/root context. | Manual browser/API |
| E8 | Callback, state, and PKCE edges | Run the explicit protocol tests and login-start test. Confirm code flow with S256 and the protocol rejection cases. Exercise missing-code and invalid-state callbacks in an unauthenticated browser; expect a safe login error and `/api/v1/me` unauthorized. Automated auth-state tests cover missing, expired, and replayed state. | Explicit protocol tests, automated, and manual browser/API |
| E9 | MCP refresh and setup ownership | Use an existing published MCP resource. Register a temporary public DCR client at `/connect/register` for `authorization_code` and `refresh_token`; complete its normal `/connect/authorize` flow as a fixture user using PKCE through 0factor. Call authenticated `/mcp` with `tools/list`; confirm tools are returned under that user's selected organization. Refresh the token and repeat `tools/list`; confirm the same user and organization binding. Attempt to use the setup-bound client from a different fixture user or organization and expect authorization denial. Record the generated client ID, then use authorized local cleanup keyed only to that ID to remove the DCR setup, OpenIddict application, and any dependent authorization/token rows created for this run; verify no test-client residue. Never record token or client-secret values. | Manual MCP/API; requires a published MCP resource and setup suitable for the run |

Run E6 before E5, while Dana's invitation is still pending. Rerun the seed to reset the invitation before repeating those two rows.

## Read-only fixture inspection

After seeding and provider sign-in, inspect only the fields needed for the assertions:

1. Join `Users` to `ExternalUserIdentities` by each stable `(mock, subject)` pair and record the subject and local ID. Inspect Dana after first login.
2. Query organization membership rows for the two fixture org IDs and project org, user ID, role, status, and default flag.
3. Query the four fixture teams and their `TeamMemberships`; compare custom-team roles independently from organization roles.
4. After each organization switch, use `/api/v1/me` to verify the active root team.
5. Inspect the Dana invitation by its fixed ID before and after acceptance; project status, user ID, invited email, role, and organization only.

Do not query or record passwords, cookies, authorization codes, state, PKCE values, or bearer tokens.

## Repeatability and cleanup

Run the seed again to restore the fixed membership roles and reset the Dana invitation to pending. The script uses one transaction and fixed IDs, checks ownership before updating existing rows, and preserves unrelated rows. It intentionally preserves Dana's local user after first login, so use a clean local database to test her true first-login path again.

Do not remove the Postgres database or 0factor volume as routine cleanup. The app exposes no public DCR deletion route, so E9 cleanup must use an authorized local OpenIddict or EF cleanup scoped to the generated client ID. Leave pre-existing setup records and IdP users unchanged.
