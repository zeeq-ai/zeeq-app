namespace Zeeq.Runtime.Server.Setup;

/// <summary>
/// Registers the data access layer and all supporting EF Core modules into the DI container.
/// </summary>
internal static class DataExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Discovers and registers all EF Core modules, then sets up the configured database provider.
        /// </summary>
        public IServiceCollection AddZeeqData(AppSettings appSettings)
        {
            if (appSettings.Database.Provider == DatabaseProvider.Postgres)
            {
                services.AddPostgres(appSettings);
            }

            return services;
        }
    }

    /// <summary>
    /// Applies pending database migrations for the configured provider.
    /// Called once at startup after the app is built.
    /// </summary>
    extension(IServiceProvider services)
    {
        /// <summary>
        /// Applies pending database migrations for the configured provider.
        /// Called once at startup after the app is built.
        /// </summary>
        public async Task UseZeeqDataAsync(AppSettings appSettings)
        {
            if (appSettings.Database.Provider == DatabaseProvider.Postgres)
            {
                await services.UsePostgresAsync();
            }
        }
    }
}
