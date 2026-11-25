using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.OpenApi;

namespace Zeeq.Runtime.Server.Setup;

/// <summary>
/// Configuration for the HTTP pipeline
/// </summary>
internal static class HttpPipelineExtensions
{
    extension(WebApplication app)
    {
        /// <summary>
        /// Allows configured development origins to read OAuth token responses from the loopback proxy.
        /// </summary>
        public void UseZeeqDevelopmentTokenCors(AppSettings appSettings)
        {
            if (!app.Environment.IsDevelopment())
            {
                return;
            }

            // OpenIddict handles this path inside authentication middleware, so CORS must run first.
            app.UseWhen(
                context => context.Request.Path == "/connect/token",
                token =>
                    token.UseCors(policy =>
                        policy
                            .WithOrigins(appSettings.Http.AllowedCorsOrigins)
                            .WithMethods(HttpMethods.Post)
                            .AllowAnyHeader()
                    )
            );
        }
    }

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
        /// Adds the OpenAPI related configuration.
        /// </summary>
        public IServiceCollection AddZeeqOpenApiConfig()
        {
            services.AddOpenApi(options =>
            {
                options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_1;
                options.AddDocumentTransformer(
                    (document, context, cancellationToken) =>
                    {
                        document.Servers = [new() { Url = "http://zeeq-api.localhost:8095/" }];
                        return Task.CompletedTask;
                    }
                );
            });

            return services;
        }

        /// <summary>
        /// Set up the reponse compression for the HTTP pipeline.  This is used to
        /// compress responses for clients that support it.
        /// See: https://learn.microsoft.com/en-us/aspnet/core/performance/response-compression
        /// </summary>
        public IServiceCollection AddZeeqResponseCompression()
        {
            services.AddResponseCompression(options =>
            {
                options.Providers.Add<GzipCompressionProvider>();
                options.Providers.Add<BrotliCompressionProvider>();
            });

            services.Configure<BrotliCompressionProviderOptions>(options =>
            {
                options.Level = CompressionLevel.Optimal;
            });

            services.Configure<GzipCompressionProviderOptions>(options =>
            {
                options.Level = CompressionLevel.Optimal;
            });

            return services;
        }
    }
}
