using System.Security.Claims;

namespace Zeeq.Platform.CodeReviews.Tests;

internal sealed class TestCodeReviewAgentExecutor : ICodeReviewAgentExecutor
{
    public string Xml { get; set; } = string.Empty;
    public string OrganizationId { get; private set; } = string.Empty;
    public string Prompt { get; private set; } = string.Empty;
    public IReadOnlyList<CodeReviewerRuntimeAgent> ActiveReviewers { get; private set; } = [];
    public bool NoAgentsActivated { get; private set; }
    public ClaimsPrincipal CallerIdentity { get; private set; } = null!;
    public CodeReviewTelemetryContext? Telemetry { get; private set; }

    public Task<string> ExecuteAsync(
        string organizationId,
        IReadOnlyList<CodeReviewerRuntimeAgent> activeReviewers,
        bool noAgentsActivated,
        CodeReviewUserPrompt codeReviewUserPrompt,
        IReadOnlyList<CodeReviewPreviousReview> previousReviews,
        ClaimsPrincipal callerIdentity,
        CodeReviewTelemetryContext telemetry,
        CancellationToken cancellationToken
    )
    {
        OrganizationId = organizationId;
        ActiveReviewers = activeReviewers;
        NoAgentsActivated = noAgentsActivated;
        Prompt = codeReviewUserPrompt.SharedPullRequestPromptBody;
        CallerIdentity = callerIdentity;
        Telemetry = telemetry;

        return Task.FromResult(Xml);
    }
}
