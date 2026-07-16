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

The Aspire app host in `host/AppHost.cs` uses a YARP reverse proxy to set up the routes.

- <http://zeeq-web.localhost:8095> is the main local entry point
- <http://zeeq-docs.localhost:8095> is the local Nuxt UI Docus docs
- <http://zeeq-api.localhost:8095/scalar> is the local Scalar UI for the API

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
