using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Zeeq.Integrations.Notion.Tests;

public sealed class NotionWebhookEndpointsTests
{
    [Test]
    public async Task MapEndpoints_UsesAnonymousRootRouteAndExplicitBodyLimit()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddRouting();
        await using var app = builder.Build();

        new NotionWebhookEndpoints().MapEndpoints(app, app);

        var route = ((IEndpointRouteBuilder)app)
            .DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(endpoint =>
                endpoint.RoutePattern.RawText == NotionWebhookEndpoints.WebhookPath
            );

        await Assert.That(route.Metadata.GetMetadata<IAllowAnonymous>()).IsNotNull();
        await Assert.That(route.Metadata.GetMetadata<RequestSizeLimitAttribute>()).IsNotNull();
        var responseStatuses = route
            .Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>()
            .Select(metadata => metadata.StatusCode)
            .ToArray();
        await Assert.That(responseStatuses).IsEquivalentTo([200, 400, 401, 404, 409, 413]);
    }
}
