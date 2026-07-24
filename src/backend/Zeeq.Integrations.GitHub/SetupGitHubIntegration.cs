using Zeeq.Core.Common;
using Zeeq.Integrations.GitHub.CheckRuns;
using Zeeq.Platform.CodeReviews;
using Humanizer;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Octokit.Webhooks;
using Octokit.Webhooks.Models;
using Serilog;

namespace Zeeq.Integrations.GitHub;

/// <summary>
/// Registers GitHub integration services.
/// </summary>
public static class SetupGitHubIntegration
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(SetupGitHubIntegration));

    extension(IServiceCollection services)
    {
        /// <summary>
        /// Adds GitHub App installation and API client services.
        /// </summary>
        public IServiceCollection AddZeeqGitHubIntegration(AppSettings appSettings)
        {
            Log.Here()
                .Information(
                    "⚙️  Adding GitHub integration: {@GitHubSettings}",
                    new
                    {
                        appSettings.GitHub.AppId,
                        appSettings.GitHub.ClientId,
                        appSettings.GitHub.AppSlug,
                        PrivateKeyPem = appSettings.GitHub.PrivateKeyPem.Truncate(24),
                        WebhookSecret = appSettings.GitHub.WebhookSecret?.Truncate(4),
                        SecretsConfigured = appSettings.GitHub.HasConfiguredSecrets,
                    }
                );

            services.AddSingleton(appSettings.GitHub);
            services.AddSingleton(appSettings.Http);
            services.AddMemoryCache();
            services.AddGitHubResilience();
            services.AddSingleton<GitHubConnectionFactory>();
            services.AddSingleton<GitHubInstallationStateTokenProtector>();
            services.AddSingleton<GitHubAppJwtFactory>();
            services.AddScoped<
                IGitHubInstallationTokenClient,
                OctokitGitHubInstallationTokenClient
            >();
            services.AddScoped<IGitHubClientFactory, OctokitGitHubClientFactory>();
            services.AddScoped<
                IGitHubInstallationTokenProvider,
                OctokitGitHubInstallationTokenProvider
            >();
            services.AddScoped<ICheckRunClient, OctokitCheckRunClient>();
            services.AddScoped<IGitHubRepositoryProvider, OctokitGitHubRepositoryProvider>();
            services.AddScoped<
                IGitHubRepositoryVisibilityClient,
                OctokitGitHubRepositoryVisibilityClient
            >();
            services.AddScoped<
                IPublicRepositoryVisibilityChecker,
                PublicRepositoryVisibilityChecker
            >();
            services.AddScoped<IGitHubCommentClientFactory, OctokitGitHubCommentClientFactory>();
            services.AddScoped<
                IGitHubCommentReactionClientFactory,
                OctokitGitHubCommentReactionClientFactory
            >();
            services.AddScoped<IGitHubCommentResolver, GitHubCommentResolver>();
            services.AddScoped<IGitHubCommentWriter, GitHubCommentWriter>();
            services.AddScoped<IGitHubPullRequestDataClient, OctokitGitHubPullRequestDataClient>();
            services.AddScoped<ICodeReviewPullRequestSource, GitHubCodeReviewPullRequestSource>();
            services.AddScoped<GitHubWebhookRepositoryGate>();
            services.AddScoped<WebhookEventProcessor, ZeeqGitHubWebhookEventProcessor>();
            services.AddScoped<IGitHubInstallationVerifier, OctokitGitHubInstallationVerifier>();

            return services;
        }
    }
}
