using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Small Markdown helpers shared by GitHub comment renderers.
/// </summary>
internal static partial class GitHubCommentMarkdown
{
    /// <summary>
    /// Removes Zeeq DOM marker-looking comments from model-provided text.
    /// </summary>
    /// <remarks>
    /// Reviewer XML is untrusted model output. It can contain normal Markdown,
    /// but it must not be able to inject or terminate Zeeq-owned DOM sections.
    /// </remarks>
    public static string SanitizeModelMarkdown(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        return ZeeqMarkerCommentRegex()
            .Replace(markdown.ReplaceLineEndings("\n").Trim(), string.Empty)
            .Trim();
    }

    /// <summary>
    /// Encodes text for use inside HTML summary elements.
    /// </summary>
    public static string EncodeSummary(string? value) =>
        WebUtility.HtmlEncode(SanitizeModelMarkdown(value));

    /// <summary>
    /// Formats a UTC timestamp in the V1 GitHub comment shape.
    /// </summary>
    public static string FormatUtc(DateTimeOffset timestamp) =>
        timestamp.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>
    /// Converts Markdown paragraphs into blockquoted paragraphs.
    /// </summary>
    public static string Blockquote(string? markdown)
    {
        var safe = SanitizeModelMarkdown(markdown);
        if (string.IsNullOrWhiteSpace(safe))
        {
            return string.Empty;
        }

        var paragraphs = safe.Split(["\n\n"], StringSplitOptions.None);

        return string.Join(
            "\n> \n",
            paragraphs.Select(paragraph =>
                string.Join(
                    "\n",
                    paragraph
                        .Split('\n')
                        .Select(line =>
                            string.IsNullOrWhiteSpace(line) ? ">" : $"> {line.TrimEnd()}"
                        )
                )
            )
        );
    }

    [GeneratedRegex(@"<!--[\s\S]*?zeeq:[\s\S]*?-->", RegexOptions.CultureInvariant)]
    private static partial Regex ZeeqMarkerCommentRegex();
}
