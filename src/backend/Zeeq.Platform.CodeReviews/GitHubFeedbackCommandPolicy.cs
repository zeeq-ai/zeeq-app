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
/// casual mentions such as "please run +zeeq later" from being treated as commands.
/// </remarks>
public static class GitHubFeedbackCommandPolicy
{
    private static readonly string[] SupportedCommandTokens = ["/zeeq", "+zeeq"];

    /// <summary>Extracts the validated triage verb, finding reference and optional reason.</summary>
    public static (string Verb, string Reference, string? Reason)? ParseTriage(string? body)
    {
        if (!TryParseCommand(body, out var command) || command != GitHubFeedbackCommand.Triage)
            return null;
        var value = body!.AsSpan().TrimStart();
        value = value[ReadFirstToken(value).Length..].TrimStart();
        var verbToken = ReadFirstToken(value);
        var verb = verbToken.ToString().ToLowerInvariant();
        value = value[verbToken.Length..].TrimStart();
        var reference = ReadFirstToken(value).ToString();
        value = value[reference.Length..].Trim();
        return (verb, reference, value.IsEmpty ? null : value.ToString());
    }

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
    public static bool TryParseCommand(string? commentBody, out GitHubFeedbackCommand command)
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

        var verb = ReadFirstToken(afterCommand);
        var rest = afterCommand[verb.Length..].TrimStart();
        var reference = ReadFirstToken(rest);
        var reason = rest[reference.Length..].Trim();
        var verbText = verb.ToString().ToLowerInvariant();
        if (
            verbText
            is "fp"
                or "false-positive"
                or "ignore"
                or "defer"
                or "wontfix"
                or "confirm"
                or "clear"
                or "undo"
        )
        {
            var requiresReason =
                verbText is "fp" or "false-positive" or "ignore" or "defer" or "wontfix";
            command =
                reference.IsEmpty || (requiresReason && reason.IsEmpty)
                    ? GitHubFeedbackCommand.TriageParseError
                    : GitHubFeedbackCommand.Triage;
            return true;
        }

        if (IsFindingReference(verb) || IsFindingReference(reference))
        {
            command = GitHubFeedbackCommand.TriageParseError;
            return true;
        }

        command = GitHubFeedbackCommand.Acknowledge;
        return true;
    }

    private static bool IsFindingReference(ReadOnlySpan<char> value) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            value.ToString(),
            @"^[0-9a-f]{5,}\.F\d+$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.CultureInvariant
        );

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

    /// <summary>A valid finding triage command.</summary>
    Triage = 3,

    /// <summary>A malformed finding triage command.</summary>
    TriageParseError = 4,
}
