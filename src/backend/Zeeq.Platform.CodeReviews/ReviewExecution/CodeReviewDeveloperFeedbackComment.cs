namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Provider-neutral human feedback that should be available to reviewer agents.
/// </summary>
/// <param name="AuthorLogin">Provider login of the human author.</param>
/// <param name="Body">Comment body after provider-specific command/noise filtering.</param>
/// <param name="CreatedAtUtc">Provider timestamp for deterministic prompt ordering.</param>
/// <param name="HtmlUrl">Optional browser URL for reviewer context.</param>
/// <param name="Path">Optional file path for inline review comments.</param>
/// <param name="Line">Optional line number for inline review comments.</param>
public sealed record CodeReviewDeveloperFeedbackComment(
    string AuthorLogin,
    string Body,
    DateTimeOffset CreatedAtUtc,
    string? HtmlUrl = null,
    string? Path = null,
    int? Line = null
);
