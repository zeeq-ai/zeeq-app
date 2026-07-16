using Microsoft.Extensions.DependencyInjection;

namespace Zeeq.Platform.Llm;

/// <summary>
/// Registers HTTP-layer LLM settings services.
/// </summary>
public static class SetupPlatformLlm
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Adds LLM settings endpoint support services.
        /// </summary>
        public IServiceCollection AddZeeqLlmPlatform()
        {
            services.AddScoped<LlmSettingsAuthorization>();

            return services;
        }
    }
}
