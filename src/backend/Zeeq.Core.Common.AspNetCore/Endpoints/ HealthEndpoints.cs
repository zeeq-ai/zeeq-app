using Zeeq.Core.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Scalar.AspNetCore;

namespace Zeeq.Core.Common.AspNetCore.Endpoints;

/// <summary>
/// Endpoints for the health checks.
/// </summary>
/// <remarks>
/// Uses static registration at the root of the app.
/// </remarks>
public static class HealthEndpoints
{
    /// <summary>
    /// Maps the health check endpoints to the given WebApplication.
    /// </summary>
    /// <param name="app">The WebApplication to map the endpoints to.</param>
    public static WebApplication MapZeeqHealthEndpoints(this WebApplication app)
    {
        app.MapGet(
                "/health",
                () =>
                    TypedResults.Ok(
                        new HealthResponse(
                            "Healthy",
                            DateTimeOffset.UtcNow,
                            GitVersionInfo.Sha,
                            GitVersionInfo.BuildTimeEst
                        )
                    )
            )
            .WithName("Health")
            .WithTags("Health")
            .ExcludeFromDescription()
            .WithSummary("Check service health.");

        app.MapGet(
                "/healthcheck",
                () =>
                    TypedResults.Ok(
                        new HealthResponse(
                            "Healthy",
                            DateTimeOffset.UtcNow,
                            GitVersionInfo.Sha,
                            GitVersionInfo.BuildTimeEst
                        )
                    )
            )
            .WithName("HealthCheck")
            .WithTags("Health")
            .ExcludeFromDescription()
            .WithSummary("Check service health (alternate route).");

        return app;
    }
}

/// <summary>
/// Response DTO for a health check.
/// </summary>
/// <param name="Status">The health status of the application.</param>
/// <param name="CheckedAtUtc">The time the health check was performed in UTC.</param>
/// <param name="Sha">The Git commit SHA of the running build.</param>
/// <param name="BuildTimeEst">The build timestamp in America/New_York.</param>
public record struct HealthResponse(
    string Status,
    DateTimeOffset CheckedAtUtc,
    string? Sha,
    string? BuildTimeEst
);
