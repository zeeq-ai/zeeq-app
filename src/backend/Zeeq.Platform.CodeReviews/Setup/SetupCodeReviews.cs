using Zeeq.Core.Common;
using Microsoft.Extensions.DependencyInjection;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Registers provider-neutral code-review workflow services.
/// </summary>
/// <remarks>
/// Runtime composition calls this from the server project. Store
/// implementations still come from the active data provider; this setup only
/// registers provider-neutral policies, handlers, and the phase-one runner.
/// </remarks>
public static class SetupCodeReviews
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Adds code-review workflow services.
        /// </summary>
        /// <remarks>
        /// The caller passes the settings slice explicitly so code-review setup
        /// does not need to know how the host binds the broader
        /// <see cref="AppSettings"/> object. That keeps this provider-neutral
        /// platform package easy to move or host in worker-only processes.
        /// </remarks>
        public IServiceCollection AddZeeqCodeReviews(CodeReviewSettings settings)
        {
            services.AddSingleton(settings);
            services.AddSingleton<CodeReviewRequestTokenProtector>();
            services.AddSingleton<CodeReviewDiffUploadTokenProtector>();
            services.AddSingleton<CodeReviewRequestLinkFactory>();
            services.AddSingleton<GitHubCommentWriteOptions>();
            services.AddSingleton<CodeReviewRuntimeDigest>();
            services.AddSingleton<ICodeReviewRuntimeStatistics>(provider =>
                provider.GetRequiredService<CodeReviewRuntimeDigest>()
            );
            services.AddScoped<CodeReviewAuthorization>();
            services.AddScoped<CodeReviewRequestService>();
            services.AddScoped<ICheckRunService, CheckRunService>();
            services.AddScoped<IGitHubCommentDomRenderer, GitHubCommentDomRenderer>();
            services.AddScoped<IGitHubCommentSectionRenderer, PullRequestHeaderSectionRenderer>();
            services.AddScoped<IGitHubCommentSectionRenderer, PullRequestStatusSectionRenderer>();
            services.AddScoped<IGitHubCommentSectionRenderer, PullRequestFindingsSectionRenderer>();
            services.AddScoped<IGitHubCommentSectionRenderer, PullRequestEvidenceSectionRenderer>();
            services.AddScoped<IGitHubCommentSectionRenderer, PullRequestSourcesSectionRenderer>();
            services.AddScoped<IGitHubCommentSectionRenderer, PullRequestActionsSectionRenderer>();
            services.AddScoped<IGitHubCommentSectionRenderer, PullRequestFooterSectionRenderer>();
            services.AddScoped<CodeReviewerAgentResolver>();
            services.AddScoped<CodeReviewLlmTierResolver>();
            services.AddSingleton<CodeReviewXmlOutputValidator>();
            services.AddScoped<CodeReviewWorkflowFactory>();
            services.AddScoped<CodeReviewAgentExecutor>();
            services.AddScoped<ICodeReviewAgentExecutor>(provider =>
                provider.GetRequiredService<CodeReviewAgentExecutor>()
            );
            services.AddScoped<ICodeReviewRunner, CodeReviewRunner>();
            services.AddScoped<ExpertCodeReviewRunner>();
            services.AddScoped<IExpertCodeReviewRunner>(provider =>
                provider.GetRequiredService<ExpertCodeReviewRunner>()
            );
            services.AddScoped<GitDiffParser>();

            return services;
        }
    }
}
