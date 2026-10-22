# Local sign-in with 0factor

Aspire runs [0factor 0.1.8](https://github.com/c4l3b/0factor/tree/v0.1.8) as the `mock-oidc` resource. It replaces `oauth2-mock-server` and provides a user picker, editable development identities, and OpenID Connect discovery. The `ZeroFactor.Aspire.Hosting` package is pinned to `0.1.8` and selects the matching container image. Docker must be running.

## Sign in

Start the stack with `mise run up`, open <http://zeeq-web.localhost:8095>, and select **0factor**. Choose one of the imported users or register a new development user. No Google, GitHub, or Microsoft client secret is needed for this provider.

The [realm import](realms/zeeq-realm.json) supplies six stable identities. `users[].id` becomes the OIDC `sub`; keep it stable when you want to reuse an existing Zeeq account.

| User | Subject | Purpose |
| --- | --- | --- |
| Legacy Mock User | `johndoe` | Reuses the previous mock provider's default Zeeq account |
| Alice Owner | `e2e-alice-owner` | Org owner and team administrator scenarios |
| Bob Multi | `e2e-bob-multi` | Different roles across two organizations |
| Cara Research | `e2e-cara-research` | Team membership and access boundaries |
| Dana Invitee | `e2e-dana-invitee` | Invitation and first-sign-in scenarios |
| Erin Outsider | `e2e-erin-outsider` | Cross-organization denial scenarios |

The import creates IdP users only. Org roles, activation, teams, and invitations remain Zeeq records; the persona notes describe the fixtures in the [e2e test plan](e2e-test-plan.md), not automatically granted permissions. A new Zeeq account can land on organization activation until its org is activated or it accepts an invitation and switches to the active org.

To select a different user reliably, sign out of Zeeq, then clear the 0factor realm session using <http://localhost:9321/realms/zeeq/protocol/openid-connect/logout> before starting sign-in again. 0factor sessions are shared by apps in the same realm, and browser cookies ignore ports. User management is at <http://localhost:9321/_0factor/realms/zeeq>.

## Configuration and identity continuity

| Setting | Value |
| --- | --- |
| Resource / host port | `mock-oidc` / `9321` |
| Discovery issuer | `http://localhost:9321/realms/zeeq` |
| Stable Zeeq provider key | `mock` |
| Public OAuth client | `mcp-openiddict-local` |
| Scopes / flow | `openid profile email` / authorization code with S256 PKCE |
| Browser callback | `http://zeeq-web.localhost:8095/auth/callback/mock` |
| Fallback callback | `http://localhost:8096/auth/callback/mock` |
| Persistent Docker volume | `zeeq-0factor-data` |

0factor discovery is realm-scoped; the previous root URL `http://localhost:9321` is no longer the issuer. The imported client restricts redirects to the two callbacks above and disables password and client-credentials grants. Zeeq continues issuing its own app cookie and MCP tokens through OpenIddict. The provider is included only in the Development environment; production login providers do not change.

Zeeq resolves existing external identities by `(provider, provider_subject)`. Keeping `mock` and `johndoe` preserves the previous default mock account even though the issuer changes. Custom accounts need their exact previous subject in the import or the registration form's **User ID** field. Reusing an email with a new subject creates a separate account. No database migration is required.

## Persistence and upgrades

The volume preserves manually created users and signing keys across container recreation. On startup, 0factor loads saved data and then reapplies realm imports. Imported realm settings and clients replace saved settings; imported users match by username/email and overwrite profile fields. Explicit imported IDs win, while users found only in saved data remain. See the upstream [import precedence rules](https://github.com/c4l3b/0factor/blob/v0.1.8/README.md#import-versus-saved-data).

After editing only the realm import, restart `mock-oidc` through Aspire. After changing the AppHost resource graph, restart the AppHost with the normal local lifecycle; rebuilding `zeeq-server` alone cannot replace the old provider resource. Coordinate with the person running a foreground AppHost before restarting it. Restarting preserves Postgres and the provider volume; deleting the provider volume removes manually created IdP users and signing keys, while the checked-in users return on import.

When upgrading 0factor, update the pinned NuGet version, inspect upstream changes, and rerun the [e2e matrix](e2e-test-plan.md). Use the development provider only in local environments: its passwordless user picker and unauthenticated management UI are intended for testing. Upstream documents protocol differences, including non-revoking logout and refresh tokens; Zeeq cookie logout must be checked separately from IdP token behavior.

## Seed the application test scenarios

The Development-only `auth-e2e-fixtures` Aspire resource runs [seed.cs](seed.cs), a .NET 10 file-based C# app. It stays stopped during normal startup. Start it explicitly to create or reset the multi-user/org/team fixtures:

```sh
aspire resource auth-e2e-fixtures start --apphost ./host --non-interactive --nologo
aspire logs auth-e2e-fixtures --apphost ./host --tail 20 --non-interactive --nologo
```

Aspire supplies the local Postgres connection through the `zeeq` connection alias and waits for the backend, including its database migrations. The app requires Development and a loopback database target. It builds fixed-ID rows through Zeeq Biome and applies bounded, transactional upserts with ownership checks. Re-running resets Dana's Alpha invitation to pending; her existing local identity and unrelated development data are retained. The existing `mock/johndoe` account is outside the fixture.

The file-based app shares the repository's EF models. It does not need a REPL connector or a second seed implementation inside the test project. To compile it without changing data, run `dotnet build build/0factor/seed.cs` from the repository root. CI performs this compile check; it does not execute the seeder.

## Verify the provider protocol

With the stack healthy, run the explicit C#/TUnit provider protocol tests from the repository root:

```sh
aspire wait mock-oidc --apphost ./host --timeout 60 --non-interactive
dotnet run --project src/backend/Zeeq.Core.Identity.Tests -- --output detailed --disable-logo --treenode-filter "/*/*/ZeroFactorProtocolIntegrationTests/*"
```

The tests check discovery, signing keys, every imported persona, both callbacks, code exchange with S256 PKCE, JSON userinfo, and rejection cases. They use the checked-in realm fixture and .NET cryptography, without Python or OpenSSL command-line tools. They stop at the registered callbacks, so Zeeq org/session behavior is verified separately by the e2e plan. These live tests run only when explicitly selected against a running local provider; ordinary CI runs do not require 0factor. Model fixtures use the C#/Biome seeder.

## Documentation affected by the switch

This repository's [setup entry point](../../README.md), [identity overview](../../src/backend/Zeeq.Core.Identity/README.md), and [identity test guide](../../src/backend/Zeeq.Core.Identity.Tests/README.md) link to this guide and the e2e plan.

The following separately maintained documentation also needs the same configuration facts. Keep these updates together when publishing the new setup instructions:

- Product docs: `zeeq-landing/web/content/docs/3.managing-zeeq/0.local-development.md` (`https://zeeq.ai/docs/managing-zeeq/local-development`). Describe Docker-backed `mock-oidc`, the **0factor** user picker, fixed subjects and activation, both callbacks, and persistence. Clarify that real-provider client secrets are optional when using 0factor.
- Zeeq KB: `zeeq://backend/openiddict-auth-stack-oauth-oidc.md`. Replace the generic local mock description with the realm issuer/client/callback contract and stable `mock` identity key.
- Zeeq KB: `zeeq://dev-tooling/aspire-cli-mcp-yarp-config.md`. Replace resource `oauth2-mock-server`/`npx` with `mock-oidc`/0factor, health check, persistent volume, and AppHost transition instructions.

Those product/KB documents are maintained outside this repository; this guide contains the complete local configuration and links to its test instructions.
