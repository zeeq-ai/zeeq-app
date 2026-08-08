using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Zeeq.Core.Common.AspNetCore.Contracts;

namespace Zeeq.Integrations.Notion;

/// <summary>Maps the anonymous Notion webhook callback.</summary>
public sealed class NotionWebhookEndpoints : IEndpoint
{
    /// <summary>Public callback path configured on a Notion webhook subscription.</summary>
    public const string WebhookPath = "/api/v1/integrations/notion/webhook/{token}";

    /// <summary>Maximum accepted Notion webhook request body.</summary>
    public const long MaxRequestBodyBytes = 1024 * 1024;

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder app, IEndpointRouteBuilder rootApp)
    {
        rootApp
            .MapPost(
                WebhookPath,
                static async Task<IResult> (
                    string token,
                    HttpRequest request,
                    [FromServices] NotionWebhookHandler handler,
                    CancellationToken cancellationToken
                ) =>
                {
                    if (request.ContentLength > MaxRequestBodyBytes)
                    {
                        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                    }

                    var rawBody = await ReadBoundedBodyAsync(request.Body, cancellationToken);
                    if (rawBody is null)
                    {
                        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                    }

                    var result = await handler.HandleAsync(
                        token,
                        rawBody,
                        request.Headers[NotionWebhookHandler.SignatureHeader].FirstOrDefault(),
                        cancellationToken
                    );

                    return Results.StatusCode(ToStatusCode(result));
                }
            )
            .WithName("ReceiveNotionWebhook")
            .WithTags("Notion")
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes))
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .AllowAnonymous();
    }

    private static async Task<byte[]?> ReadBoundedBodyAsync(
        Stream requestBody,
        CancellationToken cancellationToken
    )
    {
        var buffer = new byte[64 * 1024];
        using var body = new MemoryStream();

        while (true)
        {
            var read = await requestBody.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return body.ToArray();
            }

            if (body.Length + read > MaxRequestBodyBytes)
            {
                return null;
            }

            await body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static int ToStatusCode(NotionWebhookIngressResult result) =>
        result switch
        {
            NotionWebhookIngressResult.Accepted => StatusCodes.Status200OK,
            NotionWebhookIngressResult.NotFound => StatusCodes.Status404NotFound,
            NotionWebhookIngressResult.BadRequest => StatusCodes.Status400BadRequest,
            NotionWebhookIngressResult.Unauthorized => StatusCodes.Status401Unauthorized,
            NotionWebhookIngressResult.Conflict => StatusCodes.Status409Conflict,
            _ => throw new ArgumentOutOfRangeException(nameof(result), result, null),
        };
}
