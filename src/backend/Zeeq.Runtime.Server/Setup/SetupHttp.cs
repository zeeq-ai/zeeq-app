using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.HttpOverrides;

namespace Zeeq.Runtime.Server.Setup;

/// <summary>
/// Configuration for the HTTP pipeline
/// </summary>
internal static class HttpPipelineExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Trust X-Forwarded-* headers from the YARP reverse proxy.
        /// YARP (running as a container) sets Host to the upstream address (aspire.dev.internal)
        /// and forwards the original external host via X-Forwarded-Host.  Without this middleware,
        /// OpenIddict would build discovery-document endpoint URIs from the internal upstream host.
        /// </summary>
        public IServiceCollection AddZeeqForwardedHeadersConfig()
        {
            services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders =
                    ForwardedHeaders.XForwardedFor
                    | ForwardedHeaders.XForwardedHost
                    | ForwardedHeaders.XForwardedProto;
                // Clear the whitelist so any upstream proxy is trusted in development.
                options.KnownIPNetworks.Clear();
                options.KnownProxies.Clear();
            });

            return services;
        }

        /// <summary>
        /// Configure global JSON serialization options for the pipeline.
        /// </summary>
        public IServiceCollection AddZeeqJsonConfig()
        {
            services.ConfigureHttpJsonOptions(options =>
            {
                // options.SerializerOptions.WriteIndented = true;
                // options.SerializerOptions.IncludeFields = true;
                options.SerializerOptions.PropertyNameCaseInsensitive = true;
                options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            });

            return services;
        }

        /// <summary>
        /// Creates the cors policy to allow the frontend to call the API in development.
        /// The policy is applied globally in the pipeline configuration.
        /// </summary>
        /// <param name="appSettings">
        /// The application settings with the allowed CORS origins.
        /// </param>
        public IServiceCollection AddZeeqCorsConfig(AppSettings appSettings)
        {
            var frontendOrigin = appSettings.Http.FrontendBaseUri.TrimEnd('/');

            services.AddCors(options =>
                options.AddPolicy(
                    "api-cors-policy",
                    policy =>
                        policy
                            .WithOrigins([.. appSettings.Http.AllowedCorsOrigins, frontendOrigin])
                            .AllowAnyHeader()
                            .AllowAnyMethod()
                            .AllowCredentials()
                )
            );

            return services;
        }

        /// <summary>
        /// Adds the OpenAPI related configuration.
        /// </summary>
        public IServiceCollection AddZeeqOpenApiConfig()
        {
            services.AddOpenApi(options =>
                options.AddDocumentTransformer(
                    (document, context, cancellationToken) =>
                    {
                        document.Servers = [new() { Url = "http://zeeq-api.localhost:8095/" }];
                        return Task.CompletedTask;
                    }
                )
            );

            return services;
        }
    }
}
