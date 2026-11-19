using System.Security.Cryptography;
using System.Text;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Existing Zeeq-owned GitHub comment resolved for a target.
/// </summary>
/// <remarks>
/// Resolution combines GitHub's external comment id with the parsed DOM from
/// the live comment body. The DOM is read from GitHub every time so renderers
/// preserve sections that were written by earlier signals or future renderer
/// versions. The body SHA-256 correlates the read with an earlier write without
/// keeping the original Markdown in the record or logging its contents.
/// </remarks>
public sealed record GitHubCommentResolution(long CommentId, GitHubCommentDom Dom)
{
    /// <summary>The SHA-256 of the original body when the resolver supplies it.</summary>
    public string? BodySha256 { get; private init; }

    /// <summary>Parses the live comment and fingerprints its exact UTF-8 body.</summary>
    public static GitHubCommentResolution FromBody(
        GitHubCommentTargetSelector target,
        long commentId,
        string body
    ) =>
        new(commentId, GitHubCommentDomParser.Parse(target, body))
        {
            BodySha256 = ComputeBodySha256(body),
        };

    internal static string ComputeBodySha256(string body) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
}
