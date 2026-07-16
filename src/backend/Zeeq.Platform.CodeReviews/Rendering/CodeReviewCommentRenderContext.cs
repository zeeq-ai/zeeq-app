using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Hydrated input for pure GitHub comment section rendering.
/// </summary>
/// <remarks>
/// The GitHub comment writer performs database, artifact, and token I/O before
/// invoking the DOM renderer. Section renderers then receive this immutable
/// context and only produce Markdown patches.
/// </remarks>
public sealed record CodeReviewCommentRenderContext(
    CodeReviewRecord? Review,
    string? FindingsXml,
    CodeReviewOutputDocument? Findings,
    string? FindingsLoadError,
    CodeReviewCommentActionLinks ActionLinks,
    DateTimeOffset RenderedAtUtc,
    bool ShowNoice = false,
    bool CheckRunBlocking = false,
    CodeReviewSourceTelemetry? SourceTelemetry = null
);

/// <summary>
/// Optional action URLs available to GitHub comment section renderers.
/// </summary>
/// <param name="RequestReviewUrl">Signed URL for requesting another review.</param>
/// <param name="ProvenanceUrl">Signed URL for inspecting provenance or telemetry.</param>
/// <param name="NoiceImageUrl">Optional public URL for the clean-PR Easter egg image.</param>
/// <param name="ViewReviewUrl">Absolute frontend URL to the single-review view for this review.</param>
public sealed record CodeReviewCommentActionLinks(
    string? RequestReviewUrl = null,
    string? ProvenanceUrl = null,
    string? NoiceImageUrl = null,
    string? ViewReviewUrl = null
);
