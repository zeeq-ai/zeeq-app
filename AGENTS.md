# Agent Instructions for Zeeq

<BLOCKING_PREREQUISITE_AT_SESSION_START_DO_NOT_SKIP>
ALWAYS INCLUDE Zeeq MCP TOOLS WHEN SELECTING TOOLS

1. Use Zeeq MCP with `library = zeeq-app` for and `list_documents` to retrieve an index of documents
2. `read_document_by_path` retrieve and read `zeeq://workflow/workspace-layout-discovery-map.md`
3. Use `search_sections` and `search_code_snippets` to find targeted sections and code snippets for the objective
4. Retrieve and read full documents as needed to understand the canonical guidance and best practices for the objective
</BLOCKING_PREREQUISITE_AT_SESSION_START_DO_NOT_SKIP>

## What is Zeeq?

Zeeq (this codebase) is a tool that provides tools and context to coding agents via Model Context Protocol (MCP).  The document knowledge base (KB) and code review tools help agents produce high quality code.  Zeeq uses Zeeq to build itself.  You are connected to a remote, production Zeeq MCP server that provides context to build Zeeq itself.  (Errors from Zeeq MCP failing mean the code *in this codebase* is failing somewhere!).

This repository is `zeeq-ai/zeeq`

## Zeeq MCP

|Library|Purpose|
|--|--|
|`zeeq-app`|(Primary) Canonical coding style, technical best practices, implementation patterns|
|`zeeq-docs`|(Secondary) Architecture, product design, features, user guide|

- Prefer retrieval led reasoning and grounding using Zeeq KB MCP over pre-trained information
- Find, read, and internalize documents relevant to the objective for best practices and canonical guidance
- Use `search_sections` to efficiently search for canonical sections of documents for technical guidance (semantic)
- Use `search_code_snippets` to efficiently find canonical code snippets for coding tasks (semantic)
- Ground coding tasks and planning with Zeeq-sourced context
- Read `zeeq://backend/dotnet-csharp-best-practices.md` for `*.cs` files and working in .NET (`src/backend/**`)
- Read `zeeq://frontend/vue-pinia-nuxtui-best-practices.md` for *.vue and `*.ts` files and working in Vue with Pinia state management (`src/web/**`)
- Use `expert_code_review` after completing tasks for thoroughness and a external perspective; "code review" means Zeeq `expert_code_review` tool

<LOCAL_DEV_ONLY_ZEEQ_MCP>

Only use the local-dev Zeeq  if EXPLICITLY testing **local** behavior and asked to do so.  It is the same toolset, but running locally (`zeeq-server` itself in Aspire).
Use `library = zeeq-app`.  It does not have the full upstream content.

</LOCAL_DEV_ONLY_ZEEQ_MCP>

## Additional Tools

### Playwright MCP

|URL|Purpose|
|---|---|
|`http://zeeq-web.localhost:8095`|Local Vue front-end development (NOTE: no `/web/` base URL)|
|`http://zeeq-inspector.localhost:8095`|MCP inspector for testing MCP behavior (NOTE: no `/web/` base URL)|
|`http://app.zeeq.ai/web`|Production deployment of the app (NOTE the `/web/` base URL)|

 Pause and ask to connect to an authenticated session if needed

### Aspire MCP/CLI

- The application is running using .NET Aspire
- Use Aspire MCP to access resources, read server logs, telemetry
- `aspire ps` to list the running instances
- The `src/backend/Zeeq.Runtime.Server` resource is `zeeq-server`
- You can run `dotnet build` and then restart resources as needed
- Use the Aspire CLI `aspire resource zeeq-server rebuild` to rebuild without stopping the full stack
- Avoid stopping and starting the full Aspire runtime because it is better for it to run in the foreground; ask for help if needed
- Read `zeeq://devtools/aspire-cli-mcp-yarp-config.md` for cheatsheet

### Glider MCP

- Semantic understanding of C# using Roslyn analyzer
- Use this when traversing code, analyzing dependencies, making bulk edits, get C# file outline, checking for analyzer warnings in .NET and C# code in `src/backend/**`

### psql

- `PGPASSWORD='P@ssw0rd' psql -h localhost -p 35432 -U zeeq -d zeeq`
- `zeeq` schema
- Key tables: `auth_user_identities`, `core_organizations`, `core_users`, `core_organization_memberships`
- Deployment target is GCP CloudSQL Postgres; be aware of limitations

## Workflow

Important workflow notes:

- Read Zeeq MCP: `zeeq://workflow/feature-planning-coding-process.md`
- Use CSharpRepl to evaluate C# code inside the running local `zeeq-server` process, debug, test hypotheses, inspect DI state, invoke code while bypassing auth, write dynamic wrappers (logging, etc.).  Access via `zeeq-dotnet-repl` skill.  See the full reference guide as needed.
- A feature is not complete without docs; `docs/content` is the root for the Nuxt UI Docus document set.
- Create root level folders and files as necessary in `docs/content` to organize your docs.
