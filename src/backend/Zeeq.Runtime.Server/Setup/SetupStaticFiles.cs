using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace Zeeq.Runtime.Server.Setup;

/// <summary>
/// Setup extension methods for configuring static file serving.
/// </summary>
public static class SetupStaticFilesExtension
{
    private static void ConfigureStaticFileCaching(StaticFileResponseContext ctx)
    {
        const int durationInSeconds = 60 * 60 * 24 * 365; // 1 year
        string cacheControlValue = $"max-age={durationInSeconds}, immutable";

        var fileExtension = Path.GetExtension(ctx.File.Name).ToLowerInvariant();

        if (fileExtension == ".html")
        {
            // Short cache for HTML files
            cacheControlValue = "max-age=180, private";
        }

        // Add caching headers for static files
        ctx.Context.Response.Headers.Append("Cache-Control", cacheControlValue);
    }

    extension(WebApplication app)
    {
        /// <summary>
        /// Configures the static SPA hosting for /web (Vue) and /docs (VitePress).
        /// </summary>
        /// <remarks>
        /// The static SPA hosting is moved into here for ease of packaging and
        /// deployment.  Optimization for serving can be done via CDN to reduce
        /// load on the server app.
        /// </remarks>
        /// <param name="env">The web host environment.</param>
        public void MapStaticSpas(IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                return; // No need on local
            }

            var appFileProvider = new PhysicalFileProvider(Path.Combine(env.WebRootPath, "web"));
            var docsFileProvider = new PhysicalFileProvider(Path.Combine(env.WebRootPath, "docs"));

            // Vue SPA at /web
            app.UseDefaultFiles(
                new DefaultFilesOptions { RequestPath = "/web", FileProvider = appFileProvider }
            );
            app.UseStaticFiles(
                new StaticFileOptions
                {
                    RequestPath = "/web",
                    FileProvider = appFileProvider,
                    OnPrepareResponse = ConfigureStaticFileCaching,
                }
            );

            // VitePress docs at /docs
            app.UseDefaultFiles(
                new DefaultFilesOptions { RequestPath = "/docs", FileProvider = docsFileProvider }
            );
            app.UseStaticFiles(
                new StaticFileOptions
                {
                    RequestPath = "/docs",
                    FileProvider = docsFileProvider,
                    OnPrepareResponse = ConfigureStaticFileCaching,
                }
            );
        }

        /// <summary>
        /// Maps the static SPA fallbacks for /web and /docs so client-side routing
        /// works correctly.
        /// </summary>
        /// <param name="env">The web host environment.</param>
        public void MapSpaFallbacks(IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                return; // No need on local
            }

            // SPA fallbacks: serve index.html for any unmatched route under each prefix.
            // The :nonfile constraint skips requests that look like static assets (have a file extension)
            // so .js, .css, images, etc. return 404 rather than silently serving index.html.
            app.MapFallbackToFile("/web/{**path:nonfile}", "web/index.html");
            app.MapFallbackToFile("/docs/{**path:nonfile}", "docs/index.html");
        }
    }
}
