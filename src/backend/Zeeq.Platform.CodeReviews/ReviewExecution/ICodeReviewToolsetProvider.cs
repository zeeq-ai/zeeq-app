using Microsoft.Extensions.AI;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Creates the tools available to code-review agents.
/// </summary>
public interface ICodeReviewToolsetProvider
{
    /// <summary>
    /// Creates tools bound to the caller and the reviewer's scoped services.
    /// </summary>
    /// <param name="callerIdentity">The authenticated identity bound to every tool call.</param>
    /// <param name="services">The reviewer scope used to resolve tool services.</param>
    /// <returns>The complete toolset available to the reviewer.</returns>
    IList<AITool> CreateTools(ClaimsPrincipal callerIdentity, IServiceProvider services);
}
