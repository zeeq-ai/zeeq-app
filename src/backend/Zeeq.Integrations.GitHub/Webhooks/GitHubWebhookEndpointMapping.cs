using Zeeq.Core.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Octokit.Webhooks.AspNetCore;

namespace Zeeq.Integrations.GitHub;

/// <summary>
/// Maps the public GitHub App webhook endpoint.
/// </summary>
/// <remarks>
/// This route intentionally lives outside the authenticated Zeeq API endpoint
/// group because GitHub authenticates webhook deliveries with
/// <c>X-Hub-Signature-256</c>. The Octokit ASP.NET Core adapter owns the HTTP
/// mechanics and signature validation, while
/// <see cref="ZeeqGitHubWebhookEventProcessor" /> owns Zeeq telemetry and
/// domain dispatch decisions.
/// </remarks>
public static class GitHubWebhookEndpointMapping
{
    /// <summary>
    /// Public callback path configured on the GitHub App webhook settings.
    /// </summary>
    public const string WebhookPath = "/api/v1/integrations/github/webhook";

    /// <summary>
    /// Maximum request body accepted by the public webhook endpoint.
    /// </summary>
    /// <remarks>
    /// GitHub PR/comment webhook payloads should be well below this limit in
    /// normal operation. Keeping an explicit limit prevents the SDK endpoint
    /// from buffering unexpectedly large unauthenticated request bodies.
    /// </remarks>
    public const long MaxRequestBodyBytes = 1024 * 1024;

    extension(IEndpointRouteBuilder endpoints)
    {
        /// <summary>
        /// Maps the GitHub webhook route with SDK signature validation.
        /// </summary>
        /// <remarks>
        /// This approach allows the GitHub webhooks routes to be mapped to the
        /// webhooks SDK handler that then forwards it to the Zeeq processor.
        /// This removes the need to perform manual verification of the incoming
        /// webhook in the route and instead focuses the code on the processor.
        /// </remarks>
        /// <param name="settings">The configured GitHub App settings.</param>
        /// <returns>The endpoint convention builder for the mapped webhook route.</returns>
        public IEndpointConventionBuilder MapZeeqGitHubWebhooks(GitHubSettings settings)
        {
            return endpoints
                .MapGitHubWebhooks(WebhookPath, settings.WebhookSecret)
                .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes));
        }
    }
}
