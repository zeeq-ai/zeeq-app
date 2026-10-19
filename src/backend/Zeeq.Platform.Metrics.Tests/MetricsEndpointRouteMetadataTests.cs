using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Zeeq.Platform.Metrics.Tests;

public sealed class MetricsEndpointRouteMetadataTests
{
    private static readonly string[] IntervalOperationNames =
    [
        "GetMetricSeries",
        "GetMetricTwoDimensionalSeries",
        "GetMetricPercentiles",
        "GetMetricScatter",
        "GetMetricLeaderboard",
        "GetMetricSectionLeaderboard",
        "GetPromptLeaderboard",
        "GetReviewVolume",
        "GetReviewCosts",
        "GetReviewFindings",
        "ListFindingReviews",
        "GetMetricsOverview",
    ];

    [Test]
    public async Task IntervalRoutes_ExposeOptionalPlainStringDateQueryParameters()
    {
        var builder = WebApplication.CreateBuilder();
        foreach (
            var handlerType in new[]
            {
                typeof(GetMetricSeriesHandler),
                typeof(GetMetricTwoDimensionalSeriesHandler),
                typeof(GetMetricPercentilesHandler),
                typeof(GetMetricScatterHandler),
                typeof(GetMetricLeaderboardHandler),
                typeof(GetMetricSectionLeaderboardHandler),
                typeof(GetPromptLeaderboardHandler),
                typeof(GetReviewVolumeHandler),
                typeof(GetReviewCostsHandler),
                typeof(GetReviewFindingsHandler),
                typeof(ListFindingReviewsHandler),
                typeof(GetMetricsOverviewHandler),
                typeof(GetMetricsFilterOptionsHandler),
            }
        )
        {
            builder.Services.AddScoped(handlerType);
        }

        await using var app = builder.Build();
        new MetricsEndpoints().MapEndpoints(app.MapGroup("/api/v1"), app);

        var intervalRoutes = ((IEndpointRouteBuilder)app)
            .DataSources.SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint =>
                IntervalOperationNames.Contains(
                    endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ?? "",
                    StringComparer.Ordinal
                )
            )
            .ToDictionary(
                endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()!.EndpointName!,
                StringComparer.Ordinal
            );

        await Assert.That(intervalRoutes.Count).IsEqualTo(IntervalOperationNames.Length);

        foreach (var operationName in IntervalOperationNames)
        {
            var route = intervalRoutes[operationName];
            var bindings = route.Metadata.GetOrderedMetadata<IParameterBindingMetadata>();

            foreach (var parameterName in new[] { "window", "startDate", "endDate" })
            {
                var parameter = bindings.SingleOrDefault(binding => binding.Name == parameterName);
                await Assert.That(parameter).IsNotNull();
                await Assert.That(parameter!.IsOptional).IsTrue();
                await Assert.That(parameter.ParameterInfo!.ParameterType).IsEqualTo(typeof(string));
                await Assert
                    .That(parameter.ParameterInfo.GetCustomAttribute<RequiredAttribute>())
                    .IsNull();
                await Assert
                    .That(parameter.ParameterInfo.GetCustomAttribute<AllowedValuesAttribute>())
                    .IsNull();
            }
        }
    }
}
