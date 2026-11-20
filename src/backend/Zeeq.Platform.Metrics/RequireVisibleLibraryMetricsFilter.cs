using Microsoft.Extensions.DependencyInjection;
using Zeeq.Core.Documents;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;

namespace Zeeq.Platform.Metrics;

internal sealed class RequireVisibleLibraryMetricsFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next
    )
    {
        var http = context.HttpContext;
        var audience = http.User.GetLibraryAudience(http.Request.RouteValues["orgId"] as string);
        if (audience is null)
            return Results.NotFound();

        if (!audience.IsAdministrator)
        {
            var services = http.RequestServices;
            var visibility = services.GetRequiredService<ILibraryVisibilityStore>();
            services.GetRequiredService<LibraryMetricsScope>().VisibleNames =
                await visibility.ListVisibleNamesAsync(audience, http.RequestAborted);
        }
        return await next(context);
    }
}
