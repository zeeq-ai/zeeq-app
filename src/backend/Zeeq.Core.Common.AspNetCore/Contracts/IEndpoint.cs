using Microsoft.AspNetCore.Routing;

namespace Zeeq.Core.Common.AspNetCore.Contracts;

/// <summary>
/// Marker interface for working with minimal API endpoints and simplifying registration.
/// </summary>
public interface IEndpoint
{
    /// <summary>
    /// Maps the endpoints to the given app builder.  We'll use this to simplify
    /// global registration of endpoints.
    /// </summary>
    /// <param name="app">The application builder used for normal endpoint registration.</param>
    /// <param name="rootApp">The ungrouped application builder for routes that must opt out of shared groups.</param>
    void MapEndpoints(IEndpointRouteBuilder app, IEndpointRouteBuilder rootApp);
}
