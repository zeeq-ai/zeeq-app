using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Zeeq.Core.Common.AspNetCore;

/// <summary>
/// Converts unhandled runtime exceptions into consistent Problem Details responses.
/// </summary>
/// <remarks>
/// Minimal APIs do not use MVC exception filters, so this handler is registered
/// at the middleware boundary. That keeps endpoint handlers focused on domain
/// outcomes while still adding one telemetry/logging path for unexpected faults.
/// </remarks>
public sealed partial class ZeeqHttpExceptionHandler(
    ILogger<ZeeqHttpExceptionHandler> log,
    IHostEnvironment environment
) : IExceptionHandler
{
    /// <summary>
    /// Handles unhandled exceptions before ASP.NET Core writes its default error response.
    /// </summary>
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken
    )
    {
        const int statusCode = StatusCodes.Status500InternalServerError;

        var traceId = Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier;
        var path = httpContext.Request.Path.Value ?? string.Empty;
        var method = httpContext.Request.Method;

        LogUnhandledException(log, exception, method, path, traceId);

        Activity.Current?.SetStatus(ActivityStatusCode.Error);

        ZeeqTelemetry.AddEvent(
            [
                ("exception.type", exception.GetType().FullName),
                ("http.request.method", method),
                ("url.path", path),
                ("trace.id", traceId),
            ],
            "zeeq.exception.unhandled"
        );

        var problemDetails = new ProblemDetails()
        {
            Status = statusCode,
            Title = "An unexpected error occurred.",
            Detail = environment.IsDevelopment()
                ? exception.ToString()
                : "An unexpected error occurred while processing the request.",
            Instance = httpContext.Request.Path,
        };

        problemDetails.Extensions["traceId"] = traceId;

        httpContext.Response.StatusCode = statusCode;

        httpContext.Response.ContentType = "application/problem+json";

        await JsonSerializer.SerializeAsync(
            httpContext.Response.Body,
            problemDetails,
            cancellationToken: cancellationToken
        );

        return true;
    }

    [LoggerMessage(
        EventId = 5000,
        Level = LogLevel.Error,
        Message = "Unhandled exception while processing {Method} {Path}. TraceId: {TraceId}."
    )]
    private static partial void LogUnhandledException(
        ILogger logger,
        Exception exception,
        string method,
        string path,
        string traceId
    );
}
