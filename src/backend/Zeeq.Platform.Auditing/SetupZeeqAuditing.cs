using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zeeq.Core.Auditing;

namespace Zeeq.Platform.Auditing;

/// <summary>Registers HTTP audit actor resolution for web hosts.</summary>
/// <remarks>
/// The storage layer registers the writer, reader, and the explicit <see cref="AuditActorContext"/>
/// (background actor). Web hosts call this afterwards to resolve the actor from request claims.
/// </remarks>
public static class SetupZeeqAuditing
{
    extension(IServiceCollection services)
    {
        /// <summary>Replaces the default actor accessor with the HTTP claims-based resolver.</summary>
        public IServiceCollection AddZeeqAuditing()
        {
            services.AddHttpContextAccessor();
            services.TryAddSingleton(TimeProvider.System);
            services.AddScoped<IAuditActorAccessor, HttpAuditActorAccessor>();
            services.AddScoped<AuditReadAccess>();

            return services;
        }
    }
}
