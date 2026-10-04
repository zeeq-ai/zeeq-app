namespace Zeeq.Runtime.Server.Setup;

/// <summary>Applies the installation configuration with environment-injected secrets taking precedence.</summary>
internal static class InstallConfigurationExtensions
{
    extension(ConfigurationManager configuration)
    {
        internal ConfigurationManager AddZeeqConfigJson()
        {
            if (Environment.GetEnvironmentVariable("ZEEQ_CONFIG_JSON") is { Length: > 0 } json)
            {
                configuration
                    .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
                    .AddEnvironmentVariables();
            }
            return configuration;
        }
    }
}
