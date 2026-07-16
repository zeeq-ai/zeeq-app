using Zeeq.Core.Common;
using Zeeq.Platform.CodeReviews;
using Octokit;

namespace Zeeq.Integrations.GitHub;

/// <summary>
/// GitHub implementation of the provider-neutral code-review pull request source.
/// </summary>
/// <remarks>
/// The platform runner depends on <see cref="ICodeReviewPullRequestSource" /> so it never references
/// Octokit or this integration assembly. This adapter is the GitHub boundary: it creates an
/// installation-scoped client, fetches the PR inputs concurrently, filters Zeeq/comment-command noise,
/// and returns source-neutral DTOs for prompt construction.
/// </remarks>
internal sealed class GitHubCodeReviewPullRequestSource(
    IGitHubClientFactory clientFactory,
    IGitHubPullRequestDataClient dataClient,
    CodeReviewSettings settings
) : ICodeReviewPullRequestSource
{
    private const int MaxDeveloperFeedbackComments = 20;

    /// <inheritdoc />
    public async Task<CodeReviewPullRequestSnapshot> GetPullRequestAsync(
        CodeReviewRunRequested message,
        CancellationToken cancellationToken
    )
    {
        var (owner, repo) = GitHubRepositoryName.Parse(message.OwnerQualifiedRepoName);

        var client = await clientFactory.CreateInstallationClientForOrganizationAsync(
            message.OrganizationId,
            cancellationToken
        );
        var data = await dataClient.GetAsync(
            client,
            owner,
            repo,
            message.PullRequestNumber,
            cancellationToken
        );

        return new(
            data.Title,
            data.Body,
            [.. data.Files.Select(MapFile)],
            [.. ResolveDeveloperFeedback(data).Take(MaxDeveloperFeedbackComments)]
        );
    }

    private IEnumerable<CodeReviewDeveloperFeedbackComment> ResolveDeveloperFeedback(
        GitHubPullRequestSourceData data
    ) =>
        data
            .IssueComments.Select(MapIssueComment)
            .Concat(data.ReviewComments.Select(MapReviewComment))
            .Where(comment => IsDeveloperFeedback(comment.AuthorLogin, comment.Body))
            .OrderByDescending(comment => comment.CreatedAtUtc)
            .ThenByDescending(comment => comment.AuthorLogin, StringComparer.Ordinal);

    private static CodeReviewFileSnapshot MapFile(GitHubPullRequestFileData file) =>
        new(file.Path, file.PreviousPath, MapMutationState(file.Status), file.Patch);

    private static CodeReviewDeveloperFeedbackComment MapIssueComment(
        GitHubIssueCommentData comment
    ) => new(comment.AuthorLogin, comment.Body, comment.CreatedAtUtc, comment.HtmlUrl);

    private static CodeReviewDeveloperFeedbackComment MapReviewComment(
        GitHubReviewCommentData comment
    ) =>
        new(
            comment.AuthorLogin,
            comment.Body,
            comment.CreatedAtUtc,
            comment.HtmlUrl,
            comment.Path,
            comment.Line
        );

    private bool IsDeveloperFeedback(string authorLogin, string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        if (GitHubFeedbackCommandPolicy.IsSupportedFeedbackCommand(body))
        {
            return false;
        }

        return IsUserAuthored(authorLogin);
    }

    private bool IsUserAuthored(string authorLogin)
    {
        if (string.IsNullOrWhiteSpace(authorLogin))
        {
            return false;
        }

        var login = authorLogin.Trim();
        if (login.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (login.Contains("zeeq", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !string.Equals(login, settings.AgentIdentity, StringComparison.OrdinalIgnoreCase);
    }

    private static CodeReviewFileMutationState MapMutationState(string status) =>
        status.Trim().ToLowerInvariant() switch
        {
            "added" => CodeReviewFileMutationState.Added,
            "removed" or "deleted" => CodeReviewFileMutationState.Deleted,
            "renamed" => CodeReviewFileMutationState.Renamed,
            "copied" => CodeReviewFileMutationState.Copied,
            "binary" => CodeReviewFileMutationState.Binary,
            _ => CodeReviewFileMutationState.Modified,
        };
}

/// <summary>
/// Small GitHub PR API seam used by <see cref="GitHubCodeReviewPullRequestSource" />.
/// </summary>
internal interface IGitHubPullRequestDataClient
{
    /// <summary>
    /// Fetches GitHub PR metadata, files, issue comments, and review comments.
    /// </summary>
    Task<GitHubPullRequestSourceData> GetAsync(
        GitHubClient client,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Octokit-backed implementation of the GitHub PR source data client.
/// </summary>
internal sealed class OctokitGitHubPullRequestDataClient : IGitHubPullRequestDataClient
{
    private const int FeedbackFetchPageSize = 25;

    /// <inheritdoc />
    public async Task<GitHubPullRequestSourceData> GetAsync(
        GitHubClient client,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Perform the API actions concurrently.
        var pullRequestTask = client.PullRequest.Get(owner, repositoryName, pullRequestNumber);

        var filesTask = client.PullRequest.Files(owner, repositoryName, pullRequestNumber);

        var issueCommentsTask = client.Issue.Comment.GetAllForIssue(
            owner,
            repositoryName,
            pullRequestNumber,
            FirstFeedbackPage()
        );

        var reviewCommentsTask = client.PullRequest.ReviewComment.GetAll(
            owner,
            repositoryName,
            pullRequestNumber,
            FirstFeedbackPage()
        );

        await Task.WhenAll(pullRequestTask, filesTask, issueCommentsTask, reviewCommentsTask);

        cancellationToken.ThrowIfCancellationRequested();

        var pullRequest = await pullRequestTask;
        var files = await filesTask;
        var issueComments = await issueCommentsTask;
        var reviewComments = await reviewCommentsTask;

        return new(
            pullRequest.Title ?? string.Empty,
            pullRequest.Body ?? string.Empty,
            [.. files.Select(MapFile)],
            [.. issueComments.Select(MapIssueComment)],
            [.. reviewComments.Select(MapReviewComment)]
        );
    }

    private static ApiOptions FirstFeedbackPage() =>
        new() { PageCount = 1, PageSize = FeedbackFetchPageSize };

    private static GitHubPullRequestFileData MapFile(PullRequestFile file) =>
        new(
            file.FileName ?? string.Empty,
            file.PreviousFileName,
            file.Status ?? string.Empty,
            file.Patch ?? string.Empty
        );

    private static GitHubIssueCommentData MapIssueComment(IssueComment comment) =>
        new(
            comment.User?.Login ?? string.Empty,
            comment.Body ?? string.Empty,
            comment.CreatedAt,
            comment.HtmlUrl
        );

    private static GitHubReviewCommentData MapReviewComment(PullRequestReviewComment comment) =>
        new(
            comment.User?.Login ?? string.Empty,
            comment.Body ?? string.Empty,
            comment.CreatedAt,
            comment.HtmlUrl,
            comment.Path,
            comment.Position
        );
}

/// <summary>
/// Raw GitHub source data before platform DTO mapping and feedback filtering.
/// </summary>
internal sealed record GitHubPullRequestSourceData(
    string Title,
    string Body,
    IReadOnlyList<GitHubPullRequestFileData> Files,
    IReadOnlyList<GitHubIssueCommentData> IssueComments,
    IReadOnlyList<GitHubReviewCommentData> ReviewComments
);

/// <summary>
/// Raw GitHub pull request file data.
/// </summary>
internal sealed record GitHubPullRequestFileData(
    string Path,
    string? PreviousPath,
    string Status,
    string Patch
);

/// <summary>
/// Raw GitHub issue comment data.
/// </summary>
internal sealed record GitHubIssueCommentData(
    string AuthorLogin,
    string Body,
    DateTimeOffset CreatedAtUtc,
    string? HtmlUrl
);

/// <summary>
/// Raw GitHub pull request review comment data.
/// </summary>
internal sealed record GitHubReviewCommentData(
    string AuthorLogin,
    string Body,
    DateTimeOffset CreatedAtUtc,
    string? HtmlUrl,
    string? Path,
    int? Line
);

/// <summary>
/// Parsed owner/repository pair from a GitHub owner-qualified repository name.
/// </summary>
internal readonly record struct GitHubRepositoryName(string Owner, string Repository)
{
    /// <summary>
    /// Parses an <c>owner/repository</c> value.
    /// </summary>
    public static GitHubRepositoryName Parse(string ownerQualifiedRepoName)
    {
        var parts = ownerQualifiedRepoName.Split('/', 2, StringSplitOptions.TrimEntries);
        if (
            parts.Length != 2
            || string.IsNullOrWhiteSpace(parts[0])
            || string.IsNullOrWhiteSpace(parts[1])
        )
        {
            throw new ArgumentException(
                $"GitHub repository name must use owner/repository format. Value={ownerQualifiedRepoName}",
                nameof(ownerQualifiedRepoName)
            );
        }

        return new(parts[0], parts[1]);
    }
}
