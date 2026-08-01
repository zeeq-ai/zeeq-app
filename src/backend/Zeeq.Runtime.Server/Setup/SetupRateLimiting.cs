using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Zeeq.Runtime.Server.Setup;

/// <summary>
/// Rate limiting setup for public HTTP routes.
/// </summary>
internal static class SetupRateLimitingExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Adds named rate-limiter policies used by endpoint route mappings.
        /// </summary>
        public IServiceCollection AddZeeqRateLimiting()
        {
            services.AddRateLimiter(options =>
            {
                // Activation keys are low-volume provenance secrets; keep exchange attempts
                // intentionally restrictive to tolerate typos without allowing brute force.
                // Used by OrganizationActivationEndpoints on the authenticated exchange route.
                options.AddPolicy(
                    "organization-activation-exchange",
                    httpContext =>
                        RateLimitPartition.GetFixedWindowLimiter(
                            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                            _ => new FixedWindowRateLimiterOptions
                            {
                                PermitLimit = 5,
                                Window = TimeSpan.FromMinutes(10),
                                QueueLimit = 0,
                                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                            }
                        )
                );
            });

            return services;
        }
    }
}
