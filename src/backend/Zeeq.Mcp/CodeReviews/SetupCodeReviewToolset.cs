using Microsoft.Extensions.DependencyInjection;
using Zeeq.Mcp.CodeReviews;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Mcp;

/// <summary>
/// Registers the MCP-backed toolset used by code-review agents.
/// </summary>
public static class SetupCodeReviewToolset
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Adds the complete toolset available to code-review agents.
        /// </summary>
        public IServiceCollection AddZeeqCodeReviewToolset()
        {
            services.AddScoped<ICodeReviewToolsetProvider, CodeReviewToolsetProvider>();

            return services;
        }
    }
}
