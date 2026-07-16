using Zeeq.Core.Common.AspNetCore.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Anonymous upload endpoint for MCP expert code-review diffs.
/// </summary>
public sealed class CodeReviewMcpEndpoints : IEndpoint
{
    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder app, IEndpointRouteBuilder rootApp)
    {
        rootApp
            .MapPut(
                "/api/v1/code-review/mcp-diffs/{jobId}",
                static (
                    string jobId,
                    [FromQuery] string? token,
                    HttpRequest request,
                    [FromServices] UploadMcpCodeReviewDiffHandler handler,
                    CancellationToken cancellationToken
                ) => handler.HandleAsync(jobId, token, request.Body, cancellationToken)
            )
            .AllowAnonymous()
            .WithName("UploadMcpCodeReviewDiff")
            .WithTags("Code Reviews (MCP)")
            .WithSummary("Upload a local diff for MCP expert code review.")
            .WithDescription(
                "Accepts a raw git unified diff for a previously created MCP code-review upload URL. The anonymous route is authorized by the encrypted token."
            )
            .ExcludeFromDescription();
    }
}
