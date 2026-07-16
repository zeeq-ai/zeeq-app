---
name: zeeq-dotnet-repl
description: Use CSharpRepl to evaluate C# code inside the running local zeeq-server process.  Wrap and replace executing code to test hypotheses before making final edits.  Use the REPL to inspect the DI container, cache state, EF Core DbContext, and other services.  Very powerful tool to directly access and manipulate the running server instance without recompiling code.  See the full reference guide as needed.
---

# CSharpRepl Cheat Sheet

Full guide with recipes and gotchas: `.agents/skills/zeeq-dotnet-repl/references/CSHARP_REPL_DEBUGGING.md`.

- Evaluates C# inside the running local `zeeq-server` — real DI, database, and queue. Auto-enabled by the AppHost in local run mode; development only.
- `--eval` runs one expression and exits: ~150ms, nonzero exit code on any error, `NO_COLOR=1` for parseable output. Multi-line C#, `using` directives, and top-level `await` all work inside the string.
- The REPL globals are `services` (root `IServiceProvider`) and `Get<T>()` (singleton resolution).
- **Scoped services, one service:** `services.Use<TSvc, T>(cb)` / `await services.UseAsync<TSvc, T>(cb)`. Import with `using Zeeq;`. The return type `<T>` is inferred from the callback — specify it explicitly (C# doesn't support partial type inference).
- **Scoped services, 2–3 services (same scope):** `await services.UseAsync<TSvc1, TSvc2, T>(async (s1, s2) => ...)`. Typed params, same scope.
- **Scoped services, N services or raw DI:** `await services.UseScopedAsync<T>(async sp => { ... })`. Use when you need more than 3 services, keyed resolution, or raw `IServiceProvider`.
- **Output formatting:** chain `.AsJsonAsync()` / `.AsJsonIndentedAsync()` after any `Task<T>`-returning helper. Returns `RawOutput`, which the REPL prints unescaped (no `\"` wrapping).
- Handlers may generate their own correlation ids and ignore your parameters — pull the id from the handler's result, then search the logs for it.
- `#replace` / `#wrap` / `#patches` / `#revert` only work via `--eval` (not `--eval-file` or piping). Instance methods prepend the instance: wrap is `(orig, instance, params...)`, replace is `(instance, params...)`. Private static methods often fail with CS8917.
- Patches live in the process, not your CLI session — they persist across connects and affect real traffic until `#revert` or a restart. Always end a patching session with `#patches` then `#revert all`.
- If `connect list` is empty: `aspire logs zeeq-server --non-interactive --search 'Skipping CSharpRepl connect setup'` — usually `csharprepl` missing from `PATH` (run `mise install`).

```sh
# Find the target (pick Zeeq.Runtime.Server, not the dotnet host).
csharprepl connect list

# Sanity check: true means you're inside the process with DI captured.
NO_COLOR=1 csharprepl connect <PID> --eval 'services is not null'

# Singleton service.
NO_COLOR=1 csharprepl connect <PID> --eval 'Get<Microsoft.Extensions.Hosting.IHostEnvironment>().EnvironmentName'

# Scoped service — one service (sync).
NO_COLOR=1 csharprepl connect <PID> --eval 'using Zeeq; services.Use<Zeeq.Platform.CodeReviews.GitDiffParser, string>(s => s.GetType().FullName)'

# Scoped service — one service (async).
NO_COLOR=1 csharprepl connect <PID> --eval 'using Zeeq; using Microsoft.EntityFrameworkCore; await services.UseAsync<Zeeq.Data.Postgres.PostgresDbContext, int>(async db => await db.Organizations.CountAsync())'

# Scoped services — two services in one scope (async).
NO_COLOR=1 csharprepl connect <PID> --eval 'using Microsoft.Extensions.AI; using Zeeq; using Zeeq.Core.Llm; await services.UseAsync<ILlmClientFactory, KeyEncryptionService, string>(async (factory, keySvc) => { ... })'

# Scoped services — raw scope provider for N services or complex resolution.
NO_COLOR=1 csharprepl connect <PID> --eval 'using Microsoft.Extensions.DependencyInjection; using Zeeq; await services.UseScopedAsync<string>(async sp => { var x = sp.GetRequiredService<MyService>(); return await x.DoAsync(); })'

# EF Core query — returns typed result, chain .AsJsonAsync() for clean output.
NO_COLOR=1 csharprepl connect <PID> --eval 'using Zeeq; using Microsoft.EntityFrameworkCore; await services.UseAsync<Zeeq.Data.Postgres.PostgresDbContext, int>(async db => await db.Organizations.CountAsync())'

# EF Core query with JSON output (unescaped, no \" wrapping).
NO_COLOR=1 csharprepl connect <PID> --eval 'using Zeeq; using Microsoft.EntityFrameworkCore; await services.UseAsync<Zeeq.Data.Postgres.PostgresDbContext, List<string>>(async db => await db.Organizations.Select(o => o.Slug).Take(3).ToListAsync()).AsJsonAsync()'

# Temporary debug logging: wrap, exercise, check logs, revert.
NO_COLOR=1 csharprepl connect <PID> --eval '#wrap Zeeq.Runtime.Server.Diagnostics.PublishMessageQueueDiagnosticHandler.HandleAsync with (orig, instance, note, ct) => { Serilog.Log.Information("🔍 repl-wrap-marker note={Note}", note); return orig(instance, note, ct); }'
aspire logs zeeq-server --non-interactive --search 'repl-wrap-marker'

# Fault injection.
NO_COLOR=1 csharprepl connect <PID> --eval '#replace Zeeq.Runtime.Server.Diagnostics.PublishMessageQueueDiagnosticHandler.HandleAsync with (instance, note, ct) => throw new System.InvalidOperationException("repl-fault-injection")'

# Patch hygiene.
NO_COLOR=1 csharprepl connect <PID> --eval '#patches'
NO_COLOR=1 csharprepl connect <PID> --eval '#revert all'

# .csx script (multi-step, multi-await, shared variables).
NO_COLOR=1 csharprepl connect <PID> --eval-file my-diagnostic.csx

# Example .csx — barebones LLM call against a tenant's Azure OpenAI credential (psql from core_encrypted_values):
# my-diagnostic.csx:
#   using Microsoft.Extensions.AI;
#   using Microsoft.Extensions.DependencyInjection;
#   using Zeeq;
#   using Zeeq.Core.Llm;
#
#   await services.UseAsync<ILlmClientFactory, KeyEncryptionService, string>(async (factory, keySvc) => {
#       var key = await keySvc.DecryptKeyAsync("<orgId>", "<keyId>", System.Threading.CancellationToken.None);
#       var config = new ResolvedLlmConfiguration("<Provider>", "<Model>", key, "managed", "<Endpoint>");
#       var client = factory.CreateChatClient(config);
#       var response = await client.GetResponseAsync(
#           [new ChatMessage(ChatRole.User, "Say hello in 5 words")],
#           new ChatOptions { MaxOutputTokens = 20, Temperature = 0 },
#           System.Threading.CancellationToken.None);
#       return response.Text;
#   })
```

## Choosing the right approach

| Scenario                           | Tool                                                             |
| ---------------------------------- | ---------------------------------------------------------------- |
| One expression, returns a value    | `--eval '...'`                                                   |
| One scoped service                 | `UseAsync<TSvc, T>` / `Use<TSvc, T>`                             |
| 2–3 scoped services, same scope    | `UseAsync<TSvc1, TSvc2, T>` / `UseAsync<TSvc1, TSvc2, TSvc3, T>` |
| N scoped services or raw DI        | `UseScopedAsync<T>` / `UseScopedAsync`                           |
| Pretty-print result as JSON        | `.AsJsonAsync()` / `.AsJsonIndentedAsync()` chain                |
| Multi-step with shared variables   | `.csx` file + `--eval-file`                                      |
| Intercept a method for all callers | `#wrap` / `#replace` (via `--eval` only)                         |

Prefer `--eval` with the typed multi-service overloads when the call fits in a few lines. Write a `.csx` script only when you need shared variables across steps, complex error handling, or the script is too long to maintain inside a single-quoted bash string.

## Mistakes to Avoid

**`#wrap`/`#replace` on private static methods** — CS8917. Delegate inference fails even for simple `bool Foo(string)`. Fall back to calling through public entry points and verifying behavior indirectly.

**`using var scope` at top level in `.csx`** — CS1002. Conflicts with `using` directives. Always use `services.UseScopedAsync(...)` instead of manual scope creation.

**Nested `UseAsync` for multiple services** — each call creates a separate scope. Services from different scopes can't interact. Use `UseAsync<TSvc1, TSvc2, T>` or `UseScopedAsync` instead.

**Omitting the return type parameter** — CS1061 extension method not found. C# does not support partial type inference. Always specify all type params: `UseAsync<GitDiffParser, string>`, never `UseAsync<GitDiffParser>`. The callback's return must match `T` — the compiler validates it. Avoid over-broad types like `object` when the callback returns something concrete.

**`Get<T>()` for scoped services** — fails with "Cannot resolve scoped service from root provider." `Get<T>()` is singleton-only; scoped services always go through `Use*` or `UseScoped*`.

**`.csx` files with `try-catch` at top level** — CS1002. Top-level exception handling in scripts hits parser issues. If you need it, wrap the body in an async lambda inside `UseScopedAsync`.

**Piping without `--streamPipedInput`** — lines are concatenated and produce CS1002. Always add the flag.

**`#replace`/`#wrap` in `--eval-file`** — fails with CS1002/CS1024. These are `--eval` only.
