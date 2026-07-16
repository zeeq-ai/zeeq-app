# CSharpRepl Runtime Debugging

CSharpRepl attaches to the running local `zeeq-server` process and evaluates C# inside it — real DI services, real database, real message queue. The Aspire AppHost enables the connector automatically in local run mode, so if the stack is up, this works. Development diagnostics only: evaluated code runs with the target process's privileges and can read or mutate live state.

References:

- CSharpRepl package: <https://www.nuget.org/packages/CSharpRepl>
- Connect walkthrough: <https://fuqua.io/blog/2026/06/injecting-a-csharp-repl-into-a-running-net-process/>

## Quickstart

```sh
# Find the PID. Pick Zeeq.Runtime.Server, not the dotnet host.
csharprepl connect list

# Sanity check: prints true when the REPL is inside the process and DI was captured.
NO_COLOR=1 csharprepl connect <PID> --eval 'services is not null'
```

`--eval '<code>'` (or `-e`) runs one expression inside the process and exits. Calls complete in ~150ms and exit nonzero on compile or runtime errors, so scripts can branch on the exit code; a multi-second hang means something is actually wrong. Keep `NO_COLOR=1` for parseable output.

## Resolve and Call Services

Singletons resolve directly from the captured root provider via the `Get<T>()` REPL global:

```sh
NO_COLOR=1 csharprepl connect <PID> -e 'Get<Microsoft.Extensions.Hosting.IHostEnvironment>().EnvironmentName'
```

Scoped services need a scope. `services.Use<TSvc, T>` and `services.UseAsync<TSvc, T>` (extension members on `IServiceProvider`, defined in `src/backend/Zeeq.Runtime.Server/Setup/SetupDebugRepl.cs`) create, use, and dispose the scope around one callback. Import them with `using Zeeq;` written inline in the eval string — the `-u`/`--using` and `-r`/`--reference` flags are silently ineffective with `connect` (verified against v0.9.2). Note: both service and return types must be explicit — C# does not support partial type inference:

```sh
NO_COLOR=1 csharprepl connect <PID> --eval 'using Zeeq; services.Use<Zeeq.Platform.CodeReviews.GitDiffParser, string>(s => s.GetType().FullName)'
```

Return plain data from the callback, not the service instance itself — the scope is disposed when the call returns.

For async handlers use `UseAsync` with top-level `await` (supported in `--eval`); the callback receives the service already typed. One caution: before assuming a handler parameter (like a `note` or marker) is what shows up in the logs, check the handler's log statements — some handlers generate their own correlation id and never log what you passed in. `PublishMessageQueueDiagnosticHandler` is one of these: it ignores its `note` parameter for logging and generates its own `SmokeTestId`, so pull that id out of the handler's result rather than inventing your own marker:

```sh
NO_COLOR=1 csharprepl connect <PID> --eval 'using Zeeq; await services.UseAsync<Zeeq.Runtime.Server.Diagnostics.PublishMessageQueueDiagnosticHandler, string>(async h => {
    var result = await h.HandleAsync("repl smoke test", System.Threading.CancellationToken.None);
    return ((Microsoft.AspNetCore.Http.HttpResults.Ok<Zeeq.Runtime.Server.Diagnostics.MessageQueueDiagnosticResponse>)result.Result).Value!.SmokeTestId;
})'
```

The returned `SmokeTestId` (e.g. `mq_diag_...`) is what actually appears in the logs — use it, not the string you passed as `note`.

### Multiple scoped services in one scope

`UseAsync<TSvc, T>` resolves exactly one service. When you need two or more scoped services that share one scope, use the typed multi-service overloads:

```sh
# Two services in one scope.  <ILlmClientFactory, KeyEncryptionService, string> — two service types + the return type.
NO_COLOR=1 csharprepl connect <PID> --eval 'using Microsoft.Extensions.AI; using Zeeq; using Zeeq.Core.Llm; await services.UseAsync<ILlmClientFactory, KeyEncryptionService, string>(async (factory, keySvc) => {
    var key = await keySvc.DecryptKeyAsync("org_...", "enc_...", System.Threading.CancellationToken.None);
    var client = factory.CreateChatClient(new ResolvedLlmConfiguration("Azure OpenAI", "gpt-5.4-mini", key, "managed", "https://..."));
    var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hi")], new ChatOptions { Temperature = 0, MaxOutputTokens = 20 }, System.Threading.CancellationToken.None);
    return response.Text;
})'
```

Three-service overload: `UseAsync<TSvc1, TSvc2, TSvc3, T>`.

When you need more than three services or want the raw scoped `IServiceProvider` for other resolution patterns, use `UseScopedAsync`:

```sh
NO_COLOR=1 csharprepl connect <PID> --eval 'using Microsoft.Extensions.DependencyInjection; using Zeeq; await services.UseScopedAsync<string>(async sp => {
    var a = sp.GetRequiredService<MyServiceA>();
    var b = sp.GetKeyedService<MyServiceB>("key");
    return await a.DoSomethingAsync(b);
})'
```

Two overloads: `UseScopedAsync<T>(Func<IServiceProvider, Task<T>>)` returns a value; `UseScopedAsync(Func<IServiceProvider, Task>)` is fire-and-forget.

### JSON output with `.AsJsonAsync()`

Chain `.AsJsonAsync()` (compact) or `.AsJsonIndentedAsync()` after any `Task<T>`-returning helper. They return `RawOutput`, which the CSharpRepl pretty-printer renders unescaped — no `\"` noise wrapping every quote:

```sh
# Without .AsJsonAsync(): REPL prints the C# string literal with \" escaping.
# With .AsJsonAsync(): REPL prints raw JSON.
NO_COLOR=1 csharprepl connect <PID> --eval 'using Zeeq; using Microsoft.EntityFrameworkCore; await services.UseAsync<Zeeq.Data.Postgres.PostgresDbContext, List<string>>(async db => await db.Organizations.Select(o => o.Slug).Take(3).ToListAsync()).AsJsonAsync()'
# → ["charles-chen-fd74d249","created-org-5c65ef8d","org-a-def_..."]

NO_COLOR=1 csharprepl connect <PID> --eval 'using Zeeq; using Microsoft.EntityFrameworkCore; await services.UseAsync<Zeeq.Data.Postgres.PostgresDbContext, List<string>>(async db => await db.Organizations.Select(o => o.Slug).Take(3).ToListAsync()).AsJsonIndentedAsync()'
# → [
#     "charles-chen-fd74d249",
#     "created-org-5c65ef8d",
#     "org-a-def_..."
#   ]
```

`RawOutput` is a struct whose `ToString()` returns the raw string — the REPL calls `ToString()` when formatting output, so it bypasses the normal C# string literal escaping.

## Confirm Side Effects in Logs

If the call mutates app state, confirm the effect in the server logs using the id returned from the call (or another unique marker you control):

```sh
aspire logs zeeq-server --non-interactive --tail 500 --timestamps --search '<SmokeTestId>'
```

Expected entries for the message queue smoke test:

```text
Message queue smoke test published. SmokeTestId: <SmokeTestId>
Message queue smoke test consumed. SmokeTestId: <SmokeTestId>
```

## Longer Scripts

`--eval` accepts multi-line C#, including `using` directives and top-level `await`, so most diagnostics fit in one `--eval` string. For longer scripts, write a `.csx` file and run it with `--eval-file` (same dialect: `services`, `Get<T>()`, and `await` all available):

```sh
NO_COLOR=1 csharprepl connect <PID> --eval-file my-diagnostic.csx
```

For a sequence of independent expressions (no shared variables, no `#replace`/`#wrap` directives), piping with `--streamPipedInput` also works and needs no temp file. Each line evaluates independently and the session exits cleanly on EOF — don't send `exit`, it isn't valid C# and will error:

```sh
printf 'Environment.ProcessId\nSystem.Diagnostics.Process.GetCurrentProcess().ProcessName\n' \
  | NO_COLOR=1 csharprepl connect <PID> --streamPipedInput
```

Never pipe input without that flag: the lines get concatenated into one buffer and fail with a misleading compiler error (e.g. `CS1002: ; expected`) that looks like a syntax problem in your C# but is purely a piping artifact.

## Live Method Patching (`#replace` / `#wrap`)

Beyond calling code, the connector can swap a method's implementation in the running process without a restart. This is more invasive than everything above: a normal `--eval` call runs once and returns, but a patch changes behavior for every caller — including real request traffic — until it's reverted. Only patch process-owned code you understand, and always clean up:

```sh
# Replace a method's behavior. Static: lambda takes the original parameters.
# Instance: the instance is prepended — (instance, param1, param2) => ...
NO_COLOR=1 csharprepl connect <PID> --eval '#replace Namespace.Type.Method with (param1, param2) => expression'

# Wrap a method to observe calls without changing behavior. `orig` is the original method.
# Static: (orig, param1, param2) => ...   Instance: (orig, instance, param1, param2) => ...
# and call through with orig(instance, param1, param2).
NO_COLOR=1 csharprepl connect <PID> --eval '#wrap Namespace.Type.Method with (orig, param1, param2) => { var result = orig(param1, param2); return result; }'

# List active patches (do this before ending any session that used #replace/#wrap).
NO_COLOR=1 csharprepl connect <PID> --eval '#patches'

# Revert one patch by number, or everything.
NO_COLOR=1 csharprepl connect <PID> --eval '#revert 1'
NO_COLOR=1 csharprepl connect <PID> --eval '#revert all'
```

Verified behavior worth knowing before you rely on this:

- These directives only work through `--eval`. `--eval-file` fails with a confusing `CS1002: ; expected` for `#replace`/`#wrap` even though the same code works fine via `--eval`, and `--streamPipedInput` fails differently (`CS1024: Preprocessor directive expected`) — don't waste time debugging either as a syntax problem in your patch expression.
- Patches are stored in the running process, not the CLI session — they persist across separate `connect` invocations to the same PID and are only cleared by `#revert` or a process restart. A patch left behind by one debugging session is still live in the next one.
- `#replace`/`#wrap` can target `internal`/`private` members by name even though you can't call them directly by a dotted reference from a fresh `--eval` session (normal C# accessibility still applies to code *you* write) — call through a public entry point to exercise a patched internal method.
- End every patching session with `#patches` to confirm what's still active, then `#revert all` once you're done. Don't leave patches on a shared local Aspire instance.

## Debugging Recipes

Every example below was executed against the live process and works verbatim (swap in your PID). They compose the pieces above into the scenarios that come up most.

### Query the database through EF Core

The process's own `PostgresDbContext` is a scoped service — query it with real EF mappings instead of reaching for `psql`. EF's async operators are extension methods, so add `using Microsoft.EntityFrameworkCore;` inline alongside `using Zeeq;`:

```sh
NO_COLOR=1 csharprepl connect <PID> --eval 'using Zeeq; using Microsoft.EntityFrameworkCore; await services.UseAsync<Zeeq.Data.Postgres.PostgresDbContext, int>(async db => await db.Organizations.CountAsync())'

NO_COLOR=1 csharprepl connect <PID> --eval 'using Zeeq; using Microsoft.EntityFrameworkCore; await services.UseAsync<Zeeq.Data.Postgres.PostgresDbContext, List<string>>(async db => await db.Organizations.Select(o => o.Slug).Take(3).ToListAsync())'
```

### Discover an entity's shape when a property guess fails

If a projection fails with `CS1061` (no such property), don't iterate blind — dump the real property names with reflection and fix the query in one step:

```sh
NO_COLOR=1 csharprepl connect <PID> --eval 'using Zeeq; await services.UseAsync<Zeeq.Data.Postgres.PostgresDbContext, string>(async db => {
    var first = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstAsync(db.Organizations);
    return string.Join(", ", first.GetType().GetProperties().Select(p => p.Name));
})'
```

### Inspect configuration and evict cache entries

Read what the process actually loaded (not what you think it loaded), and drop a suspect cache entry without a restart — removing a missing key is a no-op, so eviction is always safe to try:

```sh
NO_COLOR=1 csharprepl connect <PID> --eval 'Get<Microsoft.Extensions.Configuration.IConfiguration>()["ASPNETCORE_ENVIRONMENT"]'

NO_COLOR=1 csharprepl connect <PID> --eval 'await Get<Microsoft.Extensions.Caching.Hybrid.HybridCache>().RemoveAsync("<cache-key>"); "removed"'
```

### Temporary debug logging with `#wrap`

The rebuild-free debug loop: wrap the suspect method with an injected Serilog line, exercise the code path (from the REPL or real traffic), search the logs for your marker, revert. `Serilog.Log.Information` inside the wrapper flows into the normal `zeeq-server` logs:

```sh
# 1. Inject the log line (instance method: orig, instance, then the original parameters).
NO_COLOR=1 csharprepl connect <PID> --eval '#wrap Zeeq.Runtime.Server.Diagnostics.PublishMessageQueueDiagnosticHandler.HandleAsync with (orig, instance, note, ct) => { Serilog.Log.Information("🔍 repl-wrap-marker note={Note}", note); return orig(instance, note, ct); }'

# 2. Exercise the code path, then look for the marker.
aspire logs zeeq-server --non-interactive --search 'repl-wrap-marker'

# 3. Always clean up.
NO_COLOR=1 csharprepl connect <PID> --eval '#revert all'
```

### Fault injection with `#replace`

Force a failure to see how callers, retries, or dead-lettering actually behave — no throwaway `throw` committed to the code. Remember this affects real traffic through the method until reverted:

```sh
# 1. Make the method throw (instance method: instance, then the original parameters).
NO_COLOR=1 csharprepl connect <PID> --eval '#replace Zeeq.Runtime.Server.Diagnostics.PublishMessageQueueDiagnosticHandler.HandleAsync with (instance, note, ct) => throw new System.InvalidOperationException("repl-fault-injection")'

# 2. Exercise the caller path and observe the error handling, then revert.
NO_COLOR=1 csharprepl connect <PID> --eval '#revert all'
```

### Reusable probe scripts

For a diagnostic you run repeatedly while investigating, put it in a `.csx` file — `using` directives at the top, multiple awaits, one result expression at the end:

```csharp
// server-probe.csx
using Zeeq;
using Microsoft.EntityFrameworkCore;

var env = Get<Microsoft.Extensions.Hosting.IHostEnvironment>().EnvironmentName;
var orgCount = await services.UseAsync<Zeeq.Data.Postgres.PostgresDbContext, int>(async db => await db.Organizations.CountAsync());
var cache = Get<Microsoft.Extensions.Caching.Hybrid.HybridCache>().GetType().Name;

$"env={env} orgs={orgCount} cache={cache}"
```

```sh
NO_COLOR=1 csharprepl connect <PID> --eval-file server-probe.csx
# "env=Development orgs=2 cache=DefaultHybridCache"
```

Treat these as throwaway diagnostics in a scratch directory, not repo files. If a probe earns permanence, promote it to a real development-only diagnostic endpoint (see `MessageQueueDiagnosticEndpoints.cs` for the pattern).

## Troubleshooting: `connect list` Shows Nothing

Connector setup failures are swallowed on purpose so they never block local Aspire startup. Check the `zeeq-server` startup logs for the skip message before assuming the process isn't running:

```sh
aspire logs zeeq-server --non-interactive --search 'Skipping CSharpRepl connect setup'
```

Common causes: `csharprepl` isn't on `PATH` (re-enter the repo directory so mise activates the tool, or run `mise install`), or `zeeq-server` was started before the tool was installed and needs a restart.

## Mistakes to Avoid

| Error | Cause | Fix |
|---|---|---|
| CS1002: `; expected` | `using var scope = ...` at top level after `using` directives | Use `UseScopedAsync(...)` instead of manual `CreateScope()` |
| CS1002: `; expected` in `.csx` | `try { ... } catch { ... }` at top level of script | Wrap the body in `await services.UseScopedAsync(async sp => { ... })` |
| CS1002: `; expected` from piped input | Forgot `--streamPipedInput` flag | Always add `--streamPipedInput` when piping |
| CS1002 from `--eval-file` | `#replace`/`#wrap` in a `.csx` file | These are `--eval` only |
| CS8917: delegate type could not be inferred | `#wrap`/`#replace` on private static methods (even simple ones) | Fall back to indirect verification through public entry points |
| "Cannot resolve scoped service from root provider" | `Get<T>()` on a scoped service | `Get<T>()` only resolves singletons; use `UseAsync<TSvc, T>` or `UseScopedAsync<T>` |
| Services can't interact across scopes | Nested `UseAsync` calls — separate scopes | Use `UseAsync<TSvc1, TSvc2, T>` or `UseScopedAsync<T>` |
| Extension method not found (CS1061) | Partial type inference: only service type specified, return type omitted | Specify all type params: `UseAsync<Foo, Bar, string>` |
| CS0029: cannot convert type | Callback returns a type that doesn't match the explicit `T` parameter | Align the `T` or the callback return value |
| `T=object` loses type safety | Return type specified too broadly (e.g. `UseAsync<Foo, object>` when callback returns `int`) | Use the concrete type: `UseAsync<Foo, int>` |
| `#replace` persists between sessions | Patches live in the process, not the CLI | Always run `#patches` then `#revert all` before disconnecting |

### The `using var` trap

At the top level of a `.csx` file, `using var scope = services.CreateScope()` triggers CS1002 because the parser treats `using var` as an ambiguous directive/statement. Even though `using var` is legal C# in standalone code, it conflicts inside the REPL script dialect. Always prefer `await services.UseScopedAsync(...)` — it handles scope lifetime automatically and avoids the parser ambiguity entirely.

### Bash escaping: `--eval` vs `--eval-file`

Single-quoted `--eval` strings can't contain single quotes themselves. For scripts with string literals, `.csx` files via `--eval-file` avoid all escaping problems. As a rule of thumb: if your eval string has more than one `"` pair or any `'`, write a `.csx` file.

### #wrap/#replace limitations

`#wrap` and `#replace` work well for public instance methods with simple signatures. They fail for:
- Private static methods (CS8917 — delegate inference fails)
- Async/Task-returning methods (CS8917)
- Methods with complex generics or many overloads
- Any code in `--eval-file` or piped input

For these cases, test behavior by calling through public entry points from `UseAsync` or `UseScopedAsync` instead.

## Interactive Mode

`NO_COLOR=1 csharprepl connect <PID>` (no `--eval`) opens an interactive prompt — useful for humans exploring; agents should stick to `--eval`, which is what all the examples above use. Type expressions directly and `exit` to leave.

## How It Is Wired

- `csharprepl` is installed by mise as `dotnet:CSharpRepl` (pinned version in `.config/mise.toml`).
- `host/AppHost.cs` enables the connector whenever the stack runs in local Aspire run mode, and applies the connector environment only to the `zeeq-server` project resource. `ProcessUtils.TryGetCSharpReplConnectEnvironment()` shells out to `csharprepl connect init` and forwards the emitted startup-hook values.
- `SetupDebugRepl.cs` defines the `Use`/`UseAsync` extension members in the bare `Zeeq` namespace (so the REPL import is just `using Zeeq;`). They need no registration — the REPL's `services` global is the receiver.
