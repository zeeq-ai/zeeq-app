using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Scalar.AspNetCore;
using Zeeq.Core.Common;

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
                            GitVersionInfo.BuildTimeEst,
                            GitVersionInfo.Version,
                            GitVersionInfo.VersionTag,
                            GitVersionInfo.DisplayVersion
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
                            GitVersionInfo.BuildTimeEst,
                            GitVersionInfo.Version,
                            GitVersionInfo.VersionTag,
                            GitVersionInfo.DisplayVersion
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
/// <param name="Version">The SemVer release version of the running build.</param>
/// <param name="VersionTag">The Git release tag for the running build.</param>
/// <param name="DisplayVersion">The user-facing version label.</param>
public record struct HealthResponse(
    string Status,
    DateTimeOffset CheckedAtUtc,
    string? Sha,
    string? BuildTimeEst,
    string? Version,
    string? VersionTag,
    string DisplayVersion
);
