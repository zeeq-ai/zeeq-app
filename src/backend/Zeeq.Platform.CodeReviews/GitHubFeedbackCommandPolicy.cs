namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Recognizes GitHub comment bodies that should enter the Zeeq feedback pipeline.
/// </summary>
/// <remarks>
/// GitHub sends every PR conversation comment and every PR diff comment to the
/// webhook endpoint when the app is subscribed to those events. Zeeq should
/// only enqueue feedback work for explicit command comments so normal review
/// discussion does not create immediate reactions or later review work. The
/// command must be the first non-whitespace token in the comment body. This keeps
/// casual mentions such as "please run +bb later" from being treated as commands.
/// </remarks>
public static class GitHubFeedbackCommandPolicy
{
    private static readonly string[] SupportedCommandTokens = ["/bb", "/zeeq", "+bb", "+zeeq"];

    /// <summary>
    /// Returns true when the comment body starts with a supported Zeeq command token.
    /// </summary>
    /// <param name="commentBody">Raw GitHub comment body from an issue or review-comment webhook.</param>
    public static bool IsSupportedFeedbackCommand(string? commentBody)
    {
        return TryParseCommand(commentBody, out _);
    }

    /// <summary>
    /// Parses a comment body into a command kind when the first token is a supported Zeeq command.
    /// </summary>
    /// <param name="commentBody">Raw GitHub comment body.</param>
    /// <param name="command">The parsed command kind when the comment is a supported command.</param>
    /// <returns>True when the comment body is a recognized Zeeq feedback command.</returns>
    public static bool TryParseCommand(
        string? commentBody,
        out GitHubFeedbackCommand command
    )
    {
        command = default;

        if (string.IsNullOrWhiteSpace(commentBody))
        {
            return false;
        }

        var trimmed = commentBody.AsSpan().TrimStart();
        var firstToken = ReadFirstToken(trimmed);

        var isSupported = false;
        foreach (var supportedCommandToken in SupportedCommandTokens)
        {
            if (
                firstToken.Equals(
                    supportedCommandToken.AsSpan(),
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                isSupported = true;
                break;
            }
        }

        if (!isSupported)
        {
            return false;
        }

        var afterCommand = trimmed[firstToken.Length..].TrimStart();
        if (IsBypassCheckCommand(afterCommand))
        {
            command = GitHubFeedbackCommand.BypassCheck;
            return true;
        }

        command = GitHubFeedbackCommand.Acknowledge;
        return true;
    }

    /// <summary>
    /// Reads the first command token without allocating a split array for every webhook comment.
    /// </summary>
    private static ReadOnlySpan<char> ReadFirstToken(ReadOnlySpan<char> value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsWhiteSpace(value[index]))
            {
                return value[..index];
            }
        }

        return value;
    }

    private static bool IsBypassCheckCommand(ReadOnlySpan<char> afterCommand)
    {
        var remaining = afterCommand.TrimStart();
        if (remaining.IsEmpty)
        {
            return false;
        }

        if (
            remaining.Equals("bypass check".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || remaining.Equals("bypass".AsSpan(), StringComparison.OrdinalIgnoreCase)
        )
        {
            return true;
        }

        return false;
    }
}

/// <summary>
/// Kinds of feedback commands recognized from GitHub comment bodies.
/// </summary>
public enum GitHubFeedbackCommand
{
    /// <summary>
    /// Unrecognized or non-command comment.
    /// </summary>
    None = 0,

    /// <summary>
    /// A supported Zeeq command token with no subcommand — triggers the +1 acknowledgement reaction.
    /// </summary>
    Acknowledge = 1,

    /// <summary>
    /// A <c>bypass check</c> or <c>bypass</c> subcommand — clears the blocking code-review check run.
    /// </summary>
    BypassCheck = 2,
}
