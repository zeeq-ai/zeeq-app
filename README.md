# Zeeq

Zeeq ("zeek") is an MCP server that provides teams with:

- A shared knowledge base with indexed, searchable content
- An agent code review tool and integrated GitHub code review flow using the same docs
- Visibility and telemetry into code review findings over time
- Visibility and telemetry into how agents are using the knowledge base content to affect code authoring

It is designed to be self hostable, relatively consolidated (can run entirely on Postgres; no additional infrastructure), yet operationally scalable.

## Key Technologies

|Component|Technology|
|--|--|
|Backend|.NET 10, ASP.NET Core Minimal APIs, OpenIddict, EF Core + Npgsql|
|Frontend|Vue 3, Nuxt UI|
|Database|Postgres|
|Messaging|Postgres, Google Cloud Pub/Sub|

## Setup

|Tooling|Description|Initialization|
|---|---|---|
|`dotnet`|.NET SDK for building and running the Zeeq backend|Install from <https://dotnet.microsoft.com/en-us/download>|
|`mise`|Directory management tool to auto-init directory local environment|`brew install mise`|

```shell
# Set your login, pointing to the .config/.gcloud-config pointed to by .config/mise.toml
gcloud auth login
```

The docs root defaults to the sibling `../zeeq-landing` checkout:

```text
code/zeeq/
├── zeeq-app/
└── zeeq-landing/
```

Override it when your local path differs:

```shell
dotnet user-secrets set "Parameters:docs-root" "/Users/YOUR_USER/PATH/TO/zeeq-landing" --project host/Zeeq.AspireHost.csproj
```

The AppHost reads `docs-root` when the Aspire model is built, so overrides made
through user secrets or the Aspire UI are picked up after restarting Aspire.

## General Operations

The following commands are for general operation of the stack:

```shell
# Start the stack in non-interactive mode (detached)
aspire run --detach --non-interactive

# Rebuild the backend
aspire resource zeeq-server rebuild

# Stop the stack
aspire stop
```

For full operations, you will need to set runtime secrets (see below).

## Database Migrations

Migrations are managed via the EF Core CLI using the `Zeeq.Data.Postgres.Migrations` project, which is the only project that references both the Postgres data layer and feature libraries (e.g. OpenIddict) needed to produce the full schema.

```shell
# Add a new migration
dotnet ef migrations add Migration_Name_Here \
  --project src/backend/Zeeq.Data.Postgres.Migrations

# Apply pending migrations to the database
dotnet ef database update \
  --project src/backend/Zeeq.Data.Postgres.Migrations
```

The connection string is read from `AppSettings:Database:ConnectionString`. Set it via `appsettings.Development.json` or the `AppSettings__Database__ConnectionString` environment variable.

## Local Testing

### Secrets Setup

The runtime secrets are loaded using ASP.NET user secrets.

```shell
cd src/backend/Zeeq.Runtime.Server

# Non-secret settings are in  appsettings.Development.json

# Initialize the secrets store
dotnet user-secrets init

# Add secrets to the store (see: src/backend/Zeeq.Runtime.Server/appsettings.Development.json)

# The account identifiers prefixed with the provider that designate system level admins
# The user will need to have an account first locally (or you already know the subject ID)
dotnet user-secrets set AppSettings:Platform:SystemAdminSubjects:0 secret-value

# The API key to use with the default "fast" LLM tier model.
# Not needed if using in-app configured self-managed services.
dotnet user-secrets set AppSettings:Llm:Models:Fast:ApiKey secret-value

# The API key to use for generating embeddings (always needed since there is no UI for this)
dotnet user-secrets set AppSettings:Llm:Embeddings:ApiKey secret-value

# The signing key used for importable Zeeq library export packages
dotnet user-secrets set AppSettings:Documents:LibraryExportSigningKey "$(openssl rand -base64 48 | tr -d '\n')"

# The DEV GitHub app webhook secret (other configuration directly in file)
# This is needed to test GitHub webhook flows locally since the GH app only has one webhook URL
# Alternate: configure custom webhooks: https://github.com/zeeq-ai/zeeq-app/settings/hooks
dotnet user-secrets set AppSettings:GitHub:WebhookSecret secret-value

# The DEV GitHub app private key PEM for local testing
dotnet user-secrets set AppSettings:GitHub:PrivateKeyPem secret-value

# The client secrets for OAuth providers (e.g. GitHub, Google, etc.) used for user login
# Order matches appsettings.Development.json
dotnet user-secrets set AppSettings:Auth:Providers:0:ClientSecret secret-value # Google
dotnet user-secrets set AppSettings:Auth:Providers:1:ClientSecret secret-value # GitHub

```

### Aspire and YARP

The Aspire app host in `host/AppHost.cs` uses a YARP reverse proxy to set up the routes.

- <http://zeeq-web.localhost:8095> is the main local entry point
- <http://zeeq-docs.localhost:8095/docs> is the local docs app backed by the `docs-root` Aspire parameter
- <http://zeeq-api.localhost:8095/scalar> is the local Scalar UI for the API

The docs app is launched from a separate repository root. It defaults to the
sibling `../zeeq-landing` checkout. To use a different path, set the Aspire
external parameter in user secrets, then restart Aspire so the AppHost rebuilds
the resource model:

```shell
dotnet user-secrets set "Parameters:docs-root" "/Users/cchen/code/zeeq/zeeq-landing" --project host/Zeeq.AspireHost.csproj
```

For CI or remote environments, set the same Aspire external parameter through
configuration using the environment variable form. This is also read when the
AppHost model is built:

```shell
Parameters__docs-root=/path/to/zeeq-landing
```

Note that for local testing of the full flow, the following must be true:

- The dev tunnel is necessary to consume the incoming webhook.
- The GitHub dev app must be pointed to the dev tunnel.
- The GitHub dev app must be connected to your GH account and added to at least on repo.

Webhook redelivery can be initiated from <https://github.com/organizations/zeeq-ai/settings/apps/zeeq-dev/advanced> as needed.

> [!IMPORTANT]
> If you do not get incoming webhooks, you likely need to set up the dev tunnel

Then create a PR in a test repo that has obviously wrong code (or any code) and use the draft/ready for review toggles to trigger code reviews.

## Troubleshooting Resources

- [The MCP account needs to be signed out](https://github.com/microsoft/vscode/issues/251802)

## Additional Resources

- [OTEL](https://learn.microsoft.com/en-us/azure/managed-grafana/grafana-opentelemetry-app-insights)
- [Copilot OTEL](https://github.com/microsoft/vscode-copilot-chat/blob/main/docs/monitoring/agent_monitoring.mdco)

---

## License

Zeeq
Copyright (C) 2026 Charles Chen

Zeeq is licensed under the GNU Affero General Public License,
version 3 or later. See [LICENSE](LICENSE) for details.
