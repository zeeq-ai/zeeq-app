using System.Text;
using Zeeq.Core.Common;
using Zeeq.Core.Common.AspNetCore.Contracts;
using Zeeq.Core.Common.Storage;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Handles anonymous MCP diff uploads authorized by an encrypted upload token.
/// </summary>
/// <remarks>
/// This handler is intentionally separate from the MCP tool call because the diff payload can
/// be much larger than a normal tool argument. The tool creates a short-lived upload URL, and
/// this handler accepts exactly one raw UTF-8 git diff body for that job before the runner reads
/// it from <see cref="StorageContainer.CodeReviewDiffs" />.
/// </remarks>
public sealed class UploadMcpCodeReviewDiffHandler(
    CodeReviewSettings settings,
    CodeReviewDiffUploadTokenProtector tokenProtector,
    IStorageProvider<PostgresStorageWriteOptions> storage
) : IEndpointHandler
{
    /// <summary>
    /// UTF-8 decoder configured to reject invalid bytes instead of replacing them.
    /// </summary>
    /// <remarks>
    /// Uploaded diffs are later parsed as text and included in prompts. Rejecting malformed input
    /// here keeps storage, parsing, and review generation on the same text contract.
    /// </remarks>
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    /// <summary>
    /// Validates and stores one raw git diff for a review job.
    /// </summary>
    /// <remarks>
    /// The route is anonymous, so the encrypted token is the authorization boundary. The token
    /// binds the upload to a specific job, organization, and expiry; the body is then bounded,
    /// decoded, lightly validated as a git diff, and written to temporary review storage.
    /// </remarks>
    /// <param name="jobId">
    /// The review job identifier embedded in the upload URL path and bound into the token.
    /// </param>
    /// <param name="token">
    /// The encrypted upload token created by <see cref="CodeReviewDiffUploadTokenProtector" />.
    /// </param>
    /// <param name="body">
    /// The raw request body containing a UTF-8 git unified diff, such as output from
    /// <c>git diff --binary</c> uploaded with <c>curl --data-binary</c>.
    /// </param>
    /// <param name="cancellationToken">Cancels token validation, body reading, or storage writes.</param>
    /// <returns>
    /// Unauthorized for missing, invalid, expired, or mismatched tokens; bad request for invalid
    /// diff bodies; otherwise upload metadata for the stored diff.
    /// </returns>
    public async Task<
        Results<
            UnauthorizedHttpResult,
            BadRequest<CodeReviewEndpointError>,
            Ok<CodeReviewMcpDiffUploadResponse>
        >
    > HandleAsync(string jobId, string? token, Stream body, CancellationToken cancellationToken)
    {
        // The endpoint is intentionally anonymous; the token is the complete auth check.
        if (!tokenProtector.TryUnprotect(token, out var payload) || payload!.JobId != jobId)
        {
            return TypedResults.Unauthorized();
        }

        var maxBytes = GetMaxBytes();
        var bodyBytes = await ReadBoundedBodyAsync(body, maxBytes, cancellationToken);
        if (bodyBytes is null)
        {
            return BadRequest(
                "diff_too_large",
                $"Diff upload exceeds the configured {maxBytes} byte limit."
            );
        }

        if (bodyBytes.Length == 0)
        {
            return BadRequest("empty_diff", "Diff upload body is required.");
        }

        string diffText;
        try
        {
            diffText = StrictUtf8.GetString(bodyBytes);
        }
        catch (DecoderFallbackException)
        {
            return BadRequest("invalid_utf8", "Diff upload body must be valid UTF-8 text.");
        }

        if (string.IsNullOrWhiteSpace(diffText))
        {
            return BadRequest("empty_diff", "Diff upload body is required.");
        }

        // This is a cheap shape check, not a full parser pass. The runner performs parsing after
        // the upload is read back from storage so all review paths use the same parser behavior.
        if (!diffText.Contains("diff --git ", StringComparison.Ordinal))
        {
            return BadRequest("invalid_diff", "Diff upload body must contain a git unified diff.");
        }

        // Store under the token's organization and expiry so cleanup can treat this like other
        // short-lived artifacts even if the review runner is never invoked.
        await storage.WriteTextAsync(
            ToPath(jobId),
            diffText,
            "text/plain; charset=utf-8",
            new PostgresStorageWriteOptions
            {
                OrganizationId = payload.OrganizationId,
                ExpiresAtUtc = payload.ExpiresAtUtc,
            },
            StorageContainer.CodeReviewDiffs,
            cancellationToken
        );

        return TypedResults.Ok(
            new CodeReviewMcpDiffUploadResponse(jobId, bodyBytes.Length, payload.ExpiresAtUtc)
        );
    }

    /// <summary>
    /// Builds the storage path for the uploaded diff for a review job.
    /// </summary>
    /// <remarks>
    /// For example, job <c>018ff6a5f6e57b06b4c1a0f9c13e0f12</c> is stored at
    /// <c>018ff6a5f6e57b06b4c1a0f9c13e0f12/diff.txt</c>.
    /// </remarks>
    internal static string ToPath(string jobId) => $"{jobId}/diff.txt";

    /// <summary>
    /// Reads the request body into memory while enforcing the configured maximum byte count.
    /// </summary>
    /// <remarks>
    /// Returning <see langword="null" /> distinguishes an oversized body from a valid empty body,
    /// which lets callers report <c>diff_too_large</c> before text decoding or diff validation.
    /// </remarks>
    private static async Task<byte[]?> ReadBoundedBodyAsync(
        Stream body,
        int maxBytes,
        CancellationToken cancellationToken
    )
    {
        // NOTE: A per-request byte[81920] is acceptable here — this is a low-throughput developer
        // tool and the dominant allocation is memory.ToArray() below, not the read buffer. Consider
        // ArrayPool<byte>.Shared.Rent/Return if this endpoint ever sees high-frequency traffic.
        using var memory = new MemoryStream(capacity: Math.Min(maxBytes, 81920));
        var buffer = new byte[81920];
        int bytesRead;
        while ((bytesRead = await body.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (memory.Length + bytesRead > maxBytes)
            {
                return null;
            }

            memory.Write(buffer.AsSpan(0, bytesRead));
        }

        return memory.ToArray();
    }

    /// <summary>
    /// Returns the configured upload byte limit after validating that it is usable.
    /// </summary>
    /// <remarks>
    /// A non-positive limit is a deployment/configuration error rather than a client validation
    /// error, so the handler fails fast instead of silently accepting or rejecting every upload.
    /// </remarks>
    private int GetMaxBytes()
    {
        if (settings.DiffUploadMaxBytes <= 0)
        {
            throw new InvalidOperationException(
                "AppSettings:CodeReview:DiffUploadMaxBytes must be greater than zero."
            );
        }

        return settings.DiffUploadMaxBytes;
    }

    /// <summary>
    /// Creates a typed error response with the stable code consumed by tests and clients.
    /// </summary>
    private static BadRequest<CodeReviewEndpointError> BadRequest(string code, string message) =>
        TypedResults.BadRequest(new CodeReviewEndpointError(code, message));
}
