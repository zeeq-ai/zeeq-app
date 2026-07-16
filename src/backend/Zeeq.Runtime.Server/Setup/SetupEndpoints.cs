using Zeeq.Core.Common.AspNetCore.Contracts;
using Microsoft.AspNetCore.HttpOverrides;

namespace Zeeq.Runtime.Server.Setup;

/// <summary>
/// Single static entry point to register and map extensions.  We add this here
/// since we need the endpoints in both the main app and the OpenAPI generation,
/// and this keeps the endpoint registration tidy and consistent.
/// </summary>
internal static class ApiEndpointExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers the Zeeq endpoints into DI for injection.
        /// </summary>
        public IServiceCollection AddZeeqEndpoints()
        {
            var result = services.Scan(scan =>
                scan.FromApplicationDependencies()
                    // Register all IEndpoint instances
                    .AddClasses(classes => classes.AssignableTo<IEndpoint>())
                    .AsImplementedInterfaces()
                    .WithTransientLifetime()
                    // Now register all of the handlers
                    // Handlers are injected by concrete type via [FromServices], so
                    // they must be registered as self rather than by interface.
                    .AddClasses(classes => classes.AssignableTo<IEndpointHandler>())
                    .AsSelf()
                    .WithTransientLifetime()
            );

            return result;
        }
    }

    extension(WebApplication app)
    {
        /// <summary>
        /// Entry point to register all endpoints in the application wth a root
        /// group.
        /// </summary>
        /// <param name="settings">
        /// This is null when called from the OpenAPI spec generation path.
        /// </param>
        public IApplicationBuilder MapZeeqEndpoints(AppSettings? settings)
        {
            var endpoints = app.Services.GetRequiredService<IEnumerable<IEndpoint>>();

            // The root group allows us to register global behavior and also
            // routing rules as needed.
            var rootGroup = app.MapGroup("/api/v1").RequireAuthorization();
            var adminGroup = rootGroup.MapSystemAdminGroup();

            foreach (var endpoint in endpoints)
            {
                if (endpoint is ISystemAdminEndpoint)
                {
                    endpoint.MapEndpoints(adminGroup, app);
                    continue;
                }

                endpoint.MapEndpoints(rootGroup, app);
            }

            return app;
        }
    }
}
