using System.Text.Json;

// NOTE: Deliberately the bare `Zeeq` namespace rather than the project's usual
// `Zeeq.Runtime.Server.Setup`. These members are typed into REPL eval strings by hand,
// and extension members require a `using` directive (they cannot be called fully
// qualified), so the import is `using Zeeq;` — the shortest unambiguous prefix.
namespace Zeeq;

/// <summary>
/// Scoped-service helpers on <see cref="IServiceProvider"/> for CSharpRepl runtime diagnostics.
/// </summary>
/// <remarks>
/// CSharpRepl can attach to the running <c>zeeq-server</c> process and evaluate C#
/// inside it (see <c>CSHARP_REPL_DEBUGGING.md</c>). The REPL exposes the captured root
/// provider as a global named <c>services</c>, so with <c>using Zeeq;</c> in the eval
/// expression these members read
/// naturally as <c>services.Use&lt;GitDiffParser, string&gt;(s =&gt; s.GetType().FullName)</c>.
/// Scoped services need a scope, and REPL one-liners should not hand-write scope
/// creation, so these wrap "create scope, resolve, invoke, dispose" behind one generic
/// call. No registration or startup wiring is required — the receiver is the provider
/// itself, and the members are inert unless the REPL (or other code) calls them. Anyone
/// able to evaluate code in the process can already create scopes, so this grants no
/// additional capability.
/// </remarks>
public static class DebugReplExtensions
{
    static readonly System.Text.Json.JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    extension(IServiceProvider provider)
    {
        /// <summary>
        /// Resolves <typeparamref name="TService"/> in a fresh scope, invokes the
        /// callback, and disposes the scope.
        /// </summary>
        /// <remarks>
        /// Return plain data from the callback, not the service instance itself — the
        /// scope (and the service's object graph) is disposed when this method returns.
        /// <typeparamref name="T"/> is specified explicitly at the call site
        /// (C# does not support partial type inference).
        /// Example: <c>services.Use&lt;GitDiffParser, string&gt;(s =&gt; s.GetType().FullName)</c>.
        /// </remarks>
        /// <param name="action">Callback receiving the scoped service; its return value is passed through.</param>
        /// <typeparam name="TService">The scoped (or any DI-registered) service type to resolve.</typeparam>
        /// <typeparam name="T">The callback's return type. Must be explicit — not inferred.</typeparam>
        /// <returns>Whatever the callback returns, after the scope is disposed.</returns>
        public T Use<TService, T>(Func<TService, T> action)
            where TService : notnull
        {
            using var scope = provider.CreateScope();

            return action(scope.ServiceProvider.GetRequiredService<TService>());
        }

        /// <summary>
        /// Async variant of <see cref="Use{TService, T}"/>; the scope stays alive until
        /// the callback's task completes.
        /// </summary>
        /// <remarks>
        /// CSharpRepl supports top-level <c>await</c>, so prefer
        /// <c>await services.UseAsync&lt;SomeHandler, string&gt;(async h =&gt; await h.HandleAsync(...))</c>
        /// over blocking with <c>GetAwaiter().GetResult()</c> inside
        /// <see cref="Use{TService, T}"/>.
        /// </remarks>
        /// <param name="action">Async callback receiving the scoped service; its result
        /// is passed through after the task completes.</param>
        /// <typeparam name="TService">The scoped (or any DI-registered) service type to resolve.</typeparam>
        /// <typeparam name="T">The callback's return type. Must be explicit — not inferred.</typeparam>
        /// <returns>Whatever the callback returns, after the scope is disposed.</returns>
        public async Task<T> UseAsync<TService, T>(Func<TService, Task<T>> action)
            where TService : notnull
        {
            using var scope = provider.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<TService>();

            // NOTE: The await is load-bearing: returning the task directly (dropping
            // async/await to "save" the state machine) would dispose the scope before
            // the callback completes. Fire-and-forget work inside the callback is
            // unsupported for the same reason — the scope dies when this method returns.
            var result = await action(service);

            return result;
        }

        /// <summary>
        /// Resolves <typeparamref name="TService1"/> and <typeparamref name="TService2"/>
        /// in one scope, invokes the callback, and disposes the scope when the task completes.
        /// </summary>
        /// <remarks>
        /// Both services share the same scope — unlike nesting two single-service
        /// <see cref="UseAsync{TService, T}"/> calls which would create separate scopes.
        /// </remarks>
        /// <param name="action">Async callback receiving the two scoped services.</param>
        /// <typeparam name="TService1">First scoped service type to resolve.</typeparam>
        /// <typeparam name="TService2">Second scoped service type to resolve.</typeparam>
        /// <typeparam name="T">The callback's return type. Must be explicit — not inferred.</typeparam>
        /// <returns>Whatever the callback returns, after the scope is disposed.</returns>
        public async Task<T> UseAsync<TService1, TService2, T>(
            Func<TService1, TService2, Task<T>> action
        )
            where TService1 : notnull
            where TService2 : notnull
        {
            using var scope = provider.CreateScope();
            var service1 = scope.ServiceProvider.GetRequiredService<TService1>();
            var service2 = scope.ServiceProvider.GetRequiredService<TService2>();

            // NOTE: load-bearing await — see UseAsync<TService,T> for rationale.
            var result = await action(service1, service2);

            return result;
        }

        /// <summary>
        /// Three-service variant. Same single scope, same load-bearing await rules.
        /// </summary>
        /// <param name="action">Async callback receiving the three scoped services.</param>
        /// <typeparam name="TService1">First scoped service type to resolve.</typeparam>
        /// <typeparam name="TService2">Second scoped service type to resolve.</typeparam>
        /// <typeparam name="TService3">Third scoped service type to resolve.</typeparam>
        /// <typeparam name="T">The callback's return type. Must be explicit — not inferred.</typeparam>
        /// <returns>Whatever the callback returns, after the scope is disposed.</returns>
        public async Task<T> UseAsync<TService1, TService2, TService3, T>(
            Func<TService1, TService2, TService3, Task<T>> action
        )
            where TService1 : notnull
            where TService2 : notnull
            where TService3 : notnull
        {
            using var scope = provider.CreateScope();
            var service1 = scope.ServiceProvider.GetRequiredService<TService1>();
            var service2 = scope.ServiceProvider.GetRequiredService<TService2>();
            var service3 = scope.ServiceProvider.GetRequiredService<TService3>();

            var result = await action(service1, service2, service3);

            return result;
        }

        /// <summary>
        /// Opens a fresh scope and hands the raw scoped <see cref="IServiceProvider"/> to
        /// the callback. Use when you need keyed services,
        /// <c>IEnumerable&lt;T&gt;</c> resolution, or more than three services.
        /// </summary>
        /// <remarks>
        /// The scope is disposed when the callback's task completes. Do not capture
        /// the provider or any scoped service outside the callback.
        /// </remarks>
        /// <param name="action">Async callback receiving the scoped provider.</param>
        public async Task UseScopedAsync(Func<IServiceProvider, Task> action)
        {
            using var scope = provider.CreateScope();

            await action(scope.ServiceProvider);
        }

        /// <summary>
        /// Opens a fresh scope and passes the scoped <see cref="IServiceProvider"/> to the
        /// callback, returning the callback's result.
        /// </summary>
        /// <remarks>
        /// Prefer `UseScopedAsync(Func{IServiceProvider, Task})` when
        /// no return value is needed.
        /// </remarks>
        /// <param name="action">Async callback receiving the scoped provider; its result is passed through.</param>
        /// <typeparam name="T">The callback's return type.</typeparam>
        /// <returns>Whatever the callback returns, after the scope is disposed.</returns>
        public async Task<T> UseScopedAsync<T>(Func<IServiceProvider, Task<T>> action)
        {
            using var scope = provider.CreateScope();

            return await action(scope.ServiceProvider);
        }
    }

    /// <summary>
    /// Awaits <paramref name="task"/> and serializes the result to compact JSON.
    /// Returns <see cref="RawOutput"/>, which the CSharpRepl pretty-printer renders
    /// unescaped — no <c>\"</c> wrapping on every quote.
    /// </summary>
    /// <remarks>
    /// Chain after any <c>Task&lt;T&gt;</c>-returning helper:
    /// <c>await services.UseScopedAsync&lt;List&lt;Org&gt;&gt;(...).AsJsonAsync()</c>
    /// </remarks>
    public static async Task<RawOutput> AsJsonAsync<T>(this Task<T> task)
    {
        var result = await task;

        return new RawOutput(JsonSerializer.Serialize(result));
    }

    /// <summary>
    /// Same as <see cref="AsJsonAsync{T}"/> but with indented formatting.
    /// </summary>
    public static async Task<RawOutput> AsJsonIndentedAsync<T>(this Task<T> task)
    {
        var result = await task;

        return new RawOutput(JsonSerializer.Serialize(result, SerializerOptions));
    }

    /// <summary>
    /// Wraps a raw string so the CSharpRepl pretty-printer renders it unescaped
    /// (no extra quotes or <c>\"</c> when the result is printed).
    /// </summary>
    public readonly struct RawOutput(string value)
    {
        /// <summary>
        /// Returns the string without the extra quotes or <c>\"</c> escaping that
        /// the REPL pretty-printer would normally add.
        /// </summary>
        public override string ToString() => value;
    }
}
