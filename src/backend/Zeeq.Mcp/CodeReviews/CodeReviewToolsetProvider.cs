using System.Reflection;
using System.Security.Claims;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Zeeq.Core.Documents;
using Zeeq.Mcp.Documents;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Mcp.CodeReviews;

/// <summary>
/// Creates the MCP-backed toolset available to code-review agents.
/// </summary>
public sealed class CodeReviewToolsetProvider : ICodeReviewToolsetProvider
{
    /// <inheritdoc />
    public IList<AITool> CreateTools(ClaimsPrincipal callerIdentity, IServiceProvider services)
    {
        var serviceInspector = services.GetService<IServiceProviderIsService>();

        var options = new AIFunctionFactoryOptions
        {
            ConfigureParameterBinding = parameter =>
            {
                // The caller identity is bound server-side and never exposed to the model.
                if (parameter.ParameterType == typeof(ClaimsPrincipal))
                {
                    return new()
                    {
                        ExcludeFromSchema = true,
                        BindParameter = (_, _) => callerIdentity,
                    };
                }

                // DI-backed parameters are resolved from the invocation service provider instead
                // of being deserialized from tool-call JSON.
                if (serviceInspector?.IsService(parameter.ParameterType) == true)
                {
                    return new() { ExcludeFromSchema = true, BindParameter = BindServiceParameter };
                }

                return default;
            },
        };

        // Each tool gets a fresh DI scope per invocation. Reviewer agents allow concurrent tool
        // invocation, and document stores resolve a scoped, non-thread-safe PostgresDbContext.
        return
        [
            AIFunctionFactory
                .Create(DocumentLibraryMcpTools.ListDocuments, options)
                .WithScopedServices(services, MarkCodeReviewExecutionScope),
            AIFunctionFactory
                .Create(DocumentLibraryMcpTools.ReadDocumentByPath, options)
                .WithScopedServices(services, MarkCodeReviewExecutionScope),
            AIFunctionFactory
                .Create(DocumentLibraryMcpTools.SearchDocuments, options)
                .WithScopedServices(services, MarkCodeReviewExecutionScope),
            AIFunctionFactory
                .Create(DocumentLibraryMcpTools.SearchCodeSnippets, options)
                .WithScopedServices(services, MarkCodeReviewExecutionScope),
            AIFunctionFactory
                .Create(DocumentLibraryMcpTools.SearchSections, options)
                .WithScopedServices(services, MarkCodeReviewExecutionScope),
        ];
    }

    /// <summary>
    /// Marks a tool invocation as code-review execution so document searches exclude documents
    /// that are not intended for reviewers.
    /// </summary>
    internal static void MarkCodeReviewExecutionScope(IServiceProvider scopedServices) =>
        scopedServices.GetRequiredService<DocumentSearchScope>().ForCodeReviewExecution = true;

    /// <summary>
    /// Resolves a tool service parameter from the invocation service provider.
    /// </summary>
    private static object BindServiceParameter(
        ParameterInfo parameter,
        AIFunctionArguments arguments
    )
    {
        var invocationServices =
            arguments.Services
            ?? throw new InvalidOperationException(
                $"Unable to resolve service parameter '{parameter.Name}' of type "
                    + $"'{parameter.ParameterType.FullName}' for a code-review tool invocation "
                    + "because no service provider was supplied to the agent."
            );

        return invocationServices.GetService(parameter.ParameterType)
            ?? throw new InvalidOperationException(
                $"Unable to resolve service parameter '{parameter.Name}' of type "
                    + $"'{parameter.ParameterType.FullName}' for a code-review tool invocation."
            );
    }
}
