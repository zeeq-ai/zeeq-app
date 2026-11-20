using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Zeeq.Core.Documents;

namespace Zeeq.Core.Identity;

/// <summary>Enforces direct library access for browser routes with a named library.</summary>
public sealed class RequireLibraryVisibilityFilter : IEndpointFilter
{
    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next
    )
    {
        var http = context.HttpContext;
        if (http.Request.RouteValues["name"] is string name)
        {
            var audience = http.User.GetLibraryAudience(
                http.Request.RouteValues["orgId"] as string
            );
            if (
                audience is null
                || (
                    !audience.IsAdministrator
                    && !await http
                        .RequestServices.GetRequiredService<ILibraryVisibilityStore>()
                        .CanOpenAsync(audience, name, http.RequestAborted)
                )
            )
                return Results.NotFound();
        }

        return await next(context);
    }
}
