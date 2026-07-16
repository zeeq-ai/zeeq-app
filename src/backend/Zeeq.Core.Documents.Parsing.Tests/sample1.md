---
keywords: csharp, dotnet, efcore, entity framework,
---

# C# 14 (CSharp), .NET 10, EF General Guidelines

This application is a .NET C# Model Context Protocol (MCP) server using the ModelContextProtocol C# SDK v 1.4.0 (API reference: <https://csharp.sdk.modelcontextprotocol.io/api/ModelContextProtocol.html>)

- Adhere to this expert guidance when working on .NET code in `src/backend`
- Use modern C# 14 and .NET 10 features in this codebase: `var`, switch expressions, collection initializer, lambda expressions, record types, tuples, ranges, and so on
- Make use of pattern matching for terseness and expressive code
- Keep performance in mind and use appropriate datatypes like `IReadOnlyList` and `Array` instead of `List` or `HashSet`
- In the following situations, use C# named parameters for functions and constructors:
  - Primitive inputs like `int`, `bool`, `string`
  - Long lists of parameters (more than 3; always use named parameters)
  - This helps legibility to understand the function inputs without having to read the signature
- Prefer C# 14 `extension` blocks for extension members instead of static helper and static util methods and classes
  - Extend collections like `IReadOnlyList<SomeType>` instead of writing `SomeStaticMethod(IReadOnlyList<SomeType>)`
  - **AVOID** extending pritimitive types (string, int, etc.) unless it is globally applicable

<csharp_14_extension_block>

```cs
public static class SomeExtensions
{
  extension(SomeType instance)
  {
    // Extension method
    public string SomeMethod()
      => $"SomeMethod called on {instance.Name}";

    // Extension property
    public string SomeProperty
      => $"SomeProperty called on {instance.Name}";
  }
}
```

</csharp_14_extension_block>

## Logging and OpenTelemetry (OTEL) Tracing

- This library is using Serilog and Serilog.AspNetCore
- Use the `ILogger` interface for logging in web endpoints and use Aspire MCP to check logs
- Follow the high-performang logging in .NET guidelines
  - Use `LoggerMessageAttribute` for compile time log generation on `private partial void LogSomething();`
  - Put these at the bottom of the file
  - This requires that classes are `partial` and these methods are `partial`
- Logging is visible in Aspire MCP for `zeeq-server`
- Logging is connected to OTEL traces and spans as well; use both together
- Use traces, spans, and events where it is improves the visiblity of the call flow and for troubleshooting (see `ZeeqServerTelemetry.cs` for example)

<high_performance_logging>

```cs
public partial class SomeService(ILogger<SomeService> log)
{
    // Other code

    [LoggerMessage(
        EventId = 2000,
        Level = LogLevel.Error,
        Message = "❌  Error synchronizing repository {RepoName} from {RepoUrl}."
    )]
    private partial void LogSyncError(string repoName, string repoUrl);
}
```

</high_performance_logging>

## Unit and Integration Tests with TUnit

- The application uses TUnit for unit and integration tests
- These are in the `tests` folder
- See @dotnet-csharp-tunit-integration-test.md for unit test guidance

## Automated Agent Testing

- Use the `aspire` CLI or MCP to rebuild and restart the services, access logs, and view traces
- `aspire resource zeeq-server rebuild` will rebuild the resource and restart the server
- Use the Playwright MCP to work with the UI and make changes
- Use `psql` to read and manipulate the database (do not update/delete unless asked; confirm first by describing the update/delete operation and rows affected)
  - The database is accessed at: `Host=localhost;Port=5432;Database=zeeq;Username=zeeq;Password=P@ssw0rd`
  - `zeeq` is the main EF backend for Zeeq

## Web APIs

- Follow best pracatices for Minimal web APIs
- Endpoints are placed close to their feature slice
- Name the root file `SoemthingEndpoints.cs` and implements `IEndpoint`
- `IEndpoint` routes are auto-discovered in `src/backend/Zeeq.Runtime.Server/Setup/SetupEndpoints.cs`
- Each route should have handler created: `src/backend/Zeeq.Platform.SomeFeature/SomeEndpoints.Handler.DoTheThing.cs`
- Use `TypedResults` for correct OpenAPI spec generation.
- If you need to confirm, the OpenAPI spec is in `src/web/src/api/zeeq-api.json` (it may take 3-4 seconds to regen).
- Regenerate the OpenAPI schema using `GEN=true dotnet build src/backend/Zeeq.Runtime.Server --no-restore --nologo`
  - This will automatically rebuild the TypeScript clients and types in `src/web/src/api/generated`
- There is a root route group `/api/v1`; you do not have to register this directly
  - Routes that should **not** be under `/api/v1` (e.g. `/health`) can register directly instead

<minimal_web_api_example>

```cs
// Minimal web API example endpoint
// 1. Implement the endpoint
// File: ./SomeEndpoint.cs
using Zeeq.Core.Common.AspNetCore.Contracts;

namespace Zeeq.Platform.SomeFeature;

public class SomeEndpoint : IEndpoint
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/something");

        // POST /include/the/full/route/here
        group.MapPost("/", (
            ClaimsPrincipal user,
            [FromQuery] string someParameter
            [FromServices] DoTheThingHandler handle,
             CancellationToken ct
        ) => handler.HandleAsync(user, ct))
        .WithName("DoTheThing")
        .WithTags("Something")
        .WithDescription("Some descriptive text");
    }
}

// 2. Implement the handler (testable without HTTP layer; keep HTTP layer thin and isolated)
// File: ./SomeEndpoint.Handler.DoTheThing.cs
using Zeeq.Core.Common.AspNetCore.Contracts;

namespace Zeeq.Platform.SomeFeature;

public class DoTheThingHandler(
    /* DI dependencies here in C# primary constructor*/
) : IEndpointHandler
{
    public async Task<Results<BadRequest, Ok<string>>> DoTheThing(
        /* NOTE: HttpContext is NOT passed down; HTTP handling only occurs in IEndpoint */
        ClaimsPrincipal user,
        string parameter
    )
    {
        if (string.IsNullOrEmpty(parameter))
        {
            return TypedResults.BadRequest(); // 👈 TypedResults for OpenAPI typed generation
        }

        return TypedResults.Ok($"Got your parameter: {parameter}");
    }
}
```

</minimal_web_api_example>

The file `src/backend/Zeeq.Runtime.Server/Setup/SetupEndpoints.cs` configures the endpoints:

<api_route_registration>

```cs
// API route registration
extension(IServiceCollection services)
{
    public IServiceCollection AddZeeqEndpoints()
    {
        var result = services.Scan(scan =>
            scan.FromApplicationDependencies()
                // Register all IEndpoint instances
                .AddClasses(classes => classes.AssignableTo<IEndpoint>())
                .AsImplementedInterfaces()
                .WithTransientLifetime()
                // Now register all of the handlers
                // Handlers are injected by concrete type via [FromServices], so
                // they must be registered as self rather than by interface.
                .AddClasses(classes => classes.AssignableTo<IEndpointHandler>())
                .AsSelf()
                .WithTransientLifetime()
        );

        return result;
    }
}

extension(WebApplication app)
{
    public IApplicationBuilder MapZeeqEndpoints()
    {
        var endpoints = app.Services.GetRequiredService<IEnumerable<IEndpoint>>();
        var rootGroup = app.MapGroup("/api/v1").RequireAuthorization();

        foreach (var endpoint in endpoints)
        {
            endpoint.MapEndpoints(rootGroup);
        }

        return app;
    }
}
```

</api_route_registration>

## Database Storage with Postgres and Npgsql

- Use best practices for EF Core 10 with Npgsql 10
- The server design is intended to support different storage providers (Postgres, Sqlite, etc.) so storage should be isolated and abstracted from the core application domain via interfaces
  - Interfaces should be specific to a domain slice to allow for different implementations.
  - Examples:
    - `src/backend/Zeeq.Core.Identity/Stores/IZeeqAuthStateStore.cs`
    - `src/backend/Zeeq.Core.Identity/Stores/IZeeqIdentityStore.cs`
  - Do not directly allow domain logic to take a dependency on a concrete store like `src/backend/Zeeq.Data.Postgres/PostgresDbContext.cs`; this will tie the application to a single implementation
- Not all features may be compatible between different storage providers; in these cases, try to find the BEST match for the intended behavior (e.g. `jsonb` in Postgres needs to map to `text` in Sqlite).
- Generate EF Core migrations with `dotnet ef migrations add Migration_Name_Here --project src/backend/Zeeq.Data.Postgres.Migrations`
  - Do not manually modify past migrations and do not manually create migrations from scratch; always generate and then edit if needed
  - Try to minimize migration generation; do all of your work first and then generate migrations at the end after models are finalized
- Except for global data, ALL: entities, queries, FKs, indexes should include the `OrganizationId` )(`organization_id`) because this is a distribution key that will be used in the future and will make index lookups more efficient in this database.
  - Include `organization_id` as part of the compound key

<tag_ef_calls_with_operation>

```cs
// Use `TagWithOperationCallSite` to identify the operation in the database
var token = await db
    .SomeTable.TagWithOperationCallSite("area.domain_entity.domain_action_here")
    .SingleOrDefaultAsync(
        item => item.Id == tokenId && item.OrganizationId == organizationId,
        cancellationToken
    );
```

</tag_ef_calls_with_operation>

## Language Rules and Formatting

- Use Allman style braces; always brace statements
  - Exception for `using var` for disposables; these do not need to be braced except where necessary
- 4 space tabs
- Follow idiomatic C# coding style from `learn.microsoft.com`
- When a variable initializer declares a type, use the `new()` expression or a collection initializer `[]` to make the code more terse
- When type can be inferred, omit the type declaration for terseness
- Use `dotnet csharpier format <DIR_OR_FILES>` to format files as you go along to avoid errors

## Functional Programming, `Action`, `Func<T>`

- Prefer functional approaches when possible
- Write side-effect free code by moving I/O out into a separate call or layer except where the behavior is explicitly mutating state
- This makes code more testable by allow unit tests to pass in `Action` or `Func` instead of mocks

<example_side_effect_free>

```cs
// Example of side-effect free code with

// No side effect here! (unit test this)
public async Task<string> DoSomethingAsync(Func<SomeState, Task<string>> sideEffectFn)
{
    var state = new SomeState(); // Build this up
    var result = await sideEffectFn(state);
    return result;
}

// Side effect isolated here (integration test this)
public async Task<string> DoSomethingAsync()
    => DoSomethingAsync(someState => {
        // Side effect isolated here: write to file system, make external API call, etc.
    });
```

</example_side_effect_free>

## Good Practices

- Prefer `async/await` whenever possible and concurrent code is necessary
- Where it benefits, use the Task Parallel Library (TPL) `Parallel.ForEachAsync` (remember to use concurrent data structures like `ConcurrentDictionary`, `ConcurrentBag`
- Commeting thoroughly will make it easier to read the code when refactoring
- Apply comments in code to all public members (private members in complex cases):
  - `<summary>` should be brief, concise, to-the-point
  - `<remarks>` should add details and explain "why"; document reasoning and chain of thought, related files, business context, etc.  Explain key decisions
  - `<params>` should describe the parameter, constraints, and notes where applicable
  - `<return>` documents what is returned from the call
- Classes, records, etc. should always have a comment that describes the **purpose** of the type.  Follow the same rules and use `<remarks>` to expand business context and reasoning, related files, and **flow** (how does this method fit into the larger process?)
  - Include commends on `private` methods as well; be concise according to the complexity of method.
  - For methods that build strings, include examples of the constructed value.
- Exit early in functions to reduce nesting and make code clear, concise, and easy to read
- Prefer builder pattern for complex object creation; combine with functional practices and `extension()` blocks and extension members to make fluent, composable code.
- Use parameter named parameters to make code easier to read
- Use C# named tuples to make tuples easier to use safely
- Vertical whitespace (newline) free; make code easy read for human by separating ideas
  - Single line declarations: can be dense; no vertical whitepace
  - Multi-line declaration: vertical whitespace probably good!
  - Variable declaration transition to function call or conditional logic: newline good!
  - Function call transition to function call: newline good!

<csharp_vertical_whitespace_usage>

```csharp
// Single line declaration; same concerns no vertical whitespace:
var identity = ...;
var logger = ...;
var somethingElse = ...;

// Different concerns; add vertical whitespace:
log.LogInformation("...");

var ttl = TimeSpan.FromMinutes(1); // Newline after!

var localFn = () => { ... }; // Newline after!

var result = await DoSomethingAsync(...)  // Newline after!

// Multi-line decalration; add vertical whitespace:
var oneThing = new Thing()
{
  ...
  ...
};  // Newline after!

var otherThing = new Thing()
{
  ...
};

return otherThing; // 👈 ALWAYS ISOLATE RETURN WITH NEWLINE!!
```

</csharp_vertical_whitespace_usage>

Here is a VERY IMPORANT example of good comments which help future travelers understand the flow of data and reasoning:

<idiomatic_csharp_comments>>

```csharp
/// <summary>
/// This is a summary of what the function does. It should be brief and to the point
/// </summary>
/// <remarks>
/// (Example: use remarks to explain, connect related entities, show examples)
/// This function is invoked after the primary flow in
/// <see cref="SomeOtherFunction"/> and is responsible for doing X, Y, Z. with
/// the results from `SomeOtherFunction`.  This is important because of A, B, C.
/// The setting for this is configured in <see cref="SomeConfigFileOrClass"/> and
/// is used by <see cref="SomeOtherRelatedFunction"/>.
/// </remarks>
/// <param name="typedParameter">
/// A description of this parameter and its key use case and expected values.
/// </param>
/// <param name="someParameter">
/// A string parameter we will also include an example of a value like `some::string::format`
/// to help understand the correct and expected shape here.
/// </param>
/// <typeparam name="TParam">
/// This is a typed parameter. It should be described with constraints and
/// details on how it is used.
/// </param>
/// <typeparam name="TResult">
/// This is the result parameter type description.
/// </typeparam>
/// <returns>
/// This is the return value, it is described with an example if useful for strings
/// for example.
/// </returns>
public async Task<TResult> SomeFunctionAsync<TParam>(TParam typedParameter, string someParameter)
{
    ...

    // 👇 An inline comment to explain what this important code is doing using a callout
    var result = await SomeImportantCallAsync(typedParameter, someParameter);

    ...

    // NOTE: Here is a special callout for this behavior to make it HIGHLY VISIBLE for
    // future travlers that need to understand the reasoning and decision making here
    var result = await SomeObscureAndComplexCallAsync(...);
}

```

</idiomatic_csharp_comments>

## Bad Practices to Avoid

- Avoid `Task.Run`.  This can break the `async` `ExecutionContext` and should be avoided
- Avoid using full namespaces in code; `using Some.Name.Space;` to import namespaces
