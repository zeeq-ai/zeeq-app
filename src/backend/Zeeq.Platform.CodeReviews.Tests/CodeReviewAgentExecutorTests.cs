using System.Security.Claims;
using Zeeq.Core.Documents;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using OpenIddict.Abstractions;

namespace Zeeq.Platform.CodeReviews.Tests;

/// <summary>
/// Tests for <see cref="CodeReviewAgentExecutor"/> instruction building and tool wiring.
///
/// dotnet run --project src/backend/Zeeq.Platform.CodeReviews.Tests --output detailed --disable-logo --treenode-filter "/*/*/CodeReviewAgentExecutorTests/*"
/// </summary>
public sealed class CodeReviewAgentExecutorTests
{
    [Test]
    public async Task BuildAgentSystemInstructions_IsStaticAndDoesNotContainDynamicContent()
    {
        var reviewer = new CodeReviewerRuntimeAgent(
            Id: "agent_1",
            DisplayName: "Test Reviewer",
            ReviewFacet: "Security",
            ModelTier: CodeReviewModelTier.High,
            Prompt: "Review for security.",
            ActivationConfiguration: CodeReviewerActivationConfiguration.Empty
        );

        var instructions = CodeReviewAgentExecutor.BuildAgentSystemInstructions(reviewer);

        await Assert.That(instructions).Contains("<tool_usage>");
        await Assert.That(instructions).DoesNotContain("valid_libraries=\"lib-");
        await Assert.That(instructions).DoesNotContain("<identity>");
        await Assert.That(instructions).DoesNotContain("<name use_verbatim>");
        await Assert.That(instructions).DoesNotContain("<previous_reviews>");
    }

    [Test]
    public async Task CodeReviewUserPromptCompose_WithReviewerAndPreviousReviews_ProducesOrderedContent()
    {
        var reviewer = new CodeReviewerRuntimeAgent(
            Id: "agent_security",
            DisplayName: "Security Reviewer",
            ReviewFacet: "Security",
            ModelTier: CodeReviewModelTier.High,
            Prompt: "Review for security issues.",
            ActivationConfiguration: CodeReviewerActivationConfiguration.Empty
        );
        const string sharedPullRequestPromptBody = "Apply your expert review.";
        const string previousReviews =
            "<previous_reviews><review><summary>Old finding.</summary></review></previous_reviews>";

        var composed = reviewer.ComposeUserPrompt(sharedPullRequestPromptBody, previousReviews);

        await Assert.That(composed).Contains("<identity>");
        await Assert.That(composed).Contains("<name use_verbatim>Security Reviewer</name>");
        await Assert.That(composed).Contains("<facet use_verbatim>Security</facet>");
        await Assert.That(composed).Contains(sharedPullRequestPromptBody);
        await Assert.That(composed).Contains(previousReviews);

        // Identity must come before the shared body, which must come before previous reviews.
        var identityIndex = composed.IndexOf("<identity>", StringComparison.Ordinal);
        var bodyIndex = composed.IndexOf(sharedPullRequestPromptBody, StringComparison.Ordinal);
        var previousIndex = composed.IndexOf("<previous_reviews>", StringComparison.Ordinal);

        await Assert.That(identityIndex).IsLessThan(bodyIndex);
        await Assert.That(bodyIndex).IsLessThan(previousIndex);
    }

    [Test]
    public async Task BuildLibraryTools_ProducesToolsThatDoNotExposeServerBoundParametersInSchema()
    {
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(OpenIddictConstants.Claims.Subject, "usr_123")], "test")
        );

        // The tool methods take an injected ILibraryDocumentStore. Registering it
        // lets IServiceProviderIsService recognize it as a DI-backed parameter, so
        // it is bound server-side and hidden from the model schema rather than
        // deserialized from tool-call JSON (which throws on an interface type).
        var services = new ServiceCollection()
            .AddScoped(_ => Substitute.For<ILibraryDocumentStore>())
            .BuildServiceProvider();

        var tools = CodeReviewAgentExecutor.BuildLibraryTools(principal, services);

        await Assert.That(tools.Count).IsGreaterThan(0);

        foreach (var tool in tools)
        {
            // Each tool should be an AITool; its JSON schema should not expose the
            // server-bound "user" (ClaimsPrincipal) or "store" (service) parameters.
            var jsonSchemaProp = tool.GetType().GetProperty("JsonSchema");
            if (jsonSchemaProp is not null)
            {
                var schema = jsonSchemaProp.GetValue(tool)?.ToString() ?? string.Empty;
                await Assert.That(schema).DoesNotContain("ClaimsPrincipal");
                await Assert.That(schema).DoesNotContain("\"user\"");
                await Assert.That(schema).DoesNotContain("\"store\"");
                await Assert.That(schema).DoesNotContain("ILibraryDocumentStore");
            }
        }
    }

    [Test]
    public async Task ScopedServiceAIFunction_ResolvesADistinctScopePerInvocation()
    {
        // A scoped marker yields a distinct instance per DI scope. If the wrapper reused one
        // scope, both invocations would resolve the same marker (the production DbContext-sharing
        // bug); a fresh scope per call resolves distinct markers.
        var services = new ServiceCollection().AddScoped<ScopeMarker>().BuildServiceProvider();

        // AIFunctionFactory injects the invocation AIFunctionArguments, so the probe reports which
        // scoped marker its call resolved.
        var probe = AIFunctionFactory.Create(
            (AIFunctionArguments arguments) =>
                arguments.Services!.GetRequiredService<ScopeMarker>().Id,
            name: "probe"
        );

        var wrapped = new ScopedServiceAIFunction(probe, services);

        var first = await wrapped.InvokeAsync(new AIFunctionArguments());
        var second = await wrapped.InvokeAsync(new AIFunctionArguments());

        await Assert.That(first).IsNotEqualTo(second);
    }

    [Test]
    public async Task BuildLibraryTools_WrapsEveryToolWithReviewScopeMarking()
    {
        // Locks the production wiring end-to-end: every library tool handed to a reviewer agent
        // must be a ScopedServiceAIFunction carrying MarkCodeReviewExecutionScope, otherwise that
        // tool's store queries would run unmarked and leak review-excluded documents. Delegate
        // equality compares method + target, so the method-group comparison holds.
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(OpenIddictConstants.Claims.Subject, "usr_123")], "test")
        );
        var services = new ServiceCollection()
            .AddScoped(_ => Substitute.For<ILibraryDocumentStore>())
            .BuildServiceProvider();

        var tools = CodeReviewAgentExecutor.BuildLibraryTools(principal, services);

        await Assert.That(tools.Count).IsEqualTo(6);

        foreach (var tool in tools)
        {
            var wrapped = tool as ScopedServiceAIFunction;

            await Assert.That(wrapped).IsNotNull();
            await Assert
                .That(wrapped!.ScopeConfigurator)
                .IsEqualTo(CodeReviewAgentExecutor.MarkCodeReviewExecutionScope);
        }
    }

    [Test]
    public async Task ScopedServiceAIFunction_WithMarkCodeReviewExecutionScope_MarksEachInvocationScope()
    {
        // Locks the review-path wiring: BuildLibraryTools passes MarkCodeReviewExecutionScope to
        // every library tool wrapper, and that hook must flip DocumentSearchScope inside the
        // per-invocation child scope (the stores read it there). A wrapper without the hook must
        // leave the scope unmarked — that is the interactive default.
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

        var reviewPath = new ScopedServiceAIFunction(
            probe,
            services,
            CodeReviewAgentExecutor.MarkCodeReviewExecutionScope
        );
        var defaultPath = new ScopedServiceAIFunction(probe, services);

        var reviewResult = await reviewPath.InvokeAsync(new AIFunctionArguments());
        var defaultResult = await defaultPath.InvokeAsync(new AIFunctionArguments());

        await Assert.That(reviewResult?.ToString()).IsEqualTo("marked");
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
