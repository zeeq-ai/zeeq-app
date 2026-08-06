using System.Security.Claims;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using OpenIddict.Abstractions;
using Zeeq.Core.Documents;

namespace Zeeq.Mcp.CodeReviews.Tests;

/// <summary>
/// Tests the per-invocation scoping and review filtering applied to the code-review toolset.
/// </summary>
public sealed class CodeReviewToolsetProviderTests
{
    [Test]
    public async Task ScopedServiceAIFunction_InvokeAsync_ResolvesDistinctScopePerInvocation()
    {
        // A scoped marker yields a distinct instance per DI scope. If the wrapper reused one
        // scope, both invocations would resolve the same marker.
        var services = new ServiceCollection().AddScoped<ScopeMarker>().BuildServiceProvider();

        var probe = AIFunctionFactory.Create(
            (AIFunctionArguments arguments) =>
                arguments.Services!.GetRequiredService<ScopeMarker>().Id,
            name: "probe"
        );

        var wrapped = new ScopedServiceAIFunction(probe, services);

        var first = await wrapped.InvokeAsync([]);
        var second = await wrapped.InvokeAsync([]);

        await Assert.That(first).IsNotEqualTo(second);
    }

    [Test]
    public async Task CreateTools_WrapsEveryToolWithCodeReviewScopeConfigurator()
    {
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(OpenIddictConstants.Claims.Subject, "usr_123")], "test")
        );
        var services = new ServiceCollection()
            .AddScoped(_ => Substitute.For<ILibraryDocumentStore>())
            .BuildServiceProvider();

        var tools = new CodeReviewToolsetProvider().CreateTools(principal, services);

        await Assert.That(tools.Count).IsEqualTo(5);

        foreach (var tool in tools)
        {
            var wrapped = tool as ScopedServiceAIFunction;

            await Assert.That(wrapped).IsNotNull();
            await Assert
                .That(wrapped!.ScopeConfigurator)
                .IsEqualTo(CodeReviewToolsetProvider.MarkCodeReviewExecutionScope);
        }
    }

    [Test]
    public async Task ScopedServiceAIFunction_InvokeAsync_WithScopeConfigurator_MarksInvocationScope()
    {
        var services = new ServiceCollection()
            .AddScoped<DocumentSearchScope>()
            .BuildServiceProvider();

        var probe = AIFunctionFactory.Create(
            (AIFunctionArguments arguments) =>
                arguments.Services!.GetRequiredService<DocumentSearchScope>().ForCodeReviewExecution
                    ? "marked"
                    : "unmarked",
            name: "probe"
        );

        var codeReviewTool = new ScopedServiceAIFunction(
            probe,
            services,
            CodeReviewToolsetProvider.MarkCodeReviewExecutionScope
        );
        var defaultTool = new ScopedServiceAIFunction(probe, services);

        var codeReviewResult = await codeReviewTool.InvokeAsync(new AIFunctionArguments());
        var defaultResult = await defaultTool.InvokeAsync(new AIFunctionArguments());

        await Assert.That(codeReviewResult?.ToString()).IsEqualTo("marked");
        await Assert.That(defaultResult?.ToString()).IsEqualTo("unmarked");
    }

    /// <summary>
    /// Scoped marker whose identity is unique per DI scope, used to detect scope reuse.
    /// </summary>
    private sealed class ScopeMarker
    {
        public Guid Id { get; } = Guid.NewGuid();
    }
}
