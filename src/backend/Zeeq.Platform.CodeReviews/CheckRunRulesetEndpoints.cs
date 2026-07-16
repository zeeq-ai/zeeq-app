using System.Text;
using System.Text.Json;
using Zeeq.Core.Common;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Serves the static branch-protection ruleset JSON for the Zeeq Code Review check context.
/// </summary>
/// <remarks>
/// The ruleset is identical for every repository — it only needs the Zeeq App id
/// and the constant check context name. This endpoint is registered on the API app
/// under <c>/api/v1</c> but is anonymous and excluded from the OpenAPI description
/// so the frontend "Download" button can link to it without auth and without
/// polluting the generated client surface.
/// </remarks>
public sealed class CheckRunRulesetEndpoints : IEndpoint
{
    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder app, IEndpointRouteBuilder rootApp)
    {
        app.MapGet(
                "/assets/zeeq-code-review-ruleset.json",
                static ([FromServices] IOptions<AppSettings> appSettingsOptions) =>
                    BuildRulesetFile(appSettingsOptions)
            )
            // NOTE: AllowAnonymous is intentional — the frontend Download button links directly
            // to this URL without going through the authenticated API client. The file contains
            // no user data (only the static App ID and check context name), so no auth is needed.
            .AllowAnonymous()
            .DisableAntiforgery()
            .ExcludeFromDescription();
    }

    // NOTE: The ruleset is always emitted as enforcement=active regardless of per-repository
    // Zeeq configuration. This file is a GitHub import template — the user downloads it and
    // manually applies it to their GitHub repository to enforce check runs at the GitHub level.
    // Whether Zeeq's own "block on critical/major" toggle is enabled is orthogonal to this file.
    private static IResult BuildRulesetFile(IOptions<AppSettings> appSettingsOptions)
    {
        if (!int.TryParse(appSettingsOptions.Value.GitHub.AppId, out var appId))
        {
            return TypedResults.Problem(
                "The GitHub App id is not configured.",
                statusCode: 500
            );
        }

        // NOTE: The required_status_checks rule shape (parameters.required_status_checks[] with
        // context + integration_id) matches GitHub's documented ruleset import schema. The
        // integration_id is optional but disambiguates which GitHub App owns the context name.
        var ruleset = new
        {
            name = "Require Zeeq Code Review",
            target = "branch",
            enforcement = "active",
            conditions = new
            {
                ref_name = new
                {
                    include = new[] { "~DEFAULT_BRANCH" },
                    exclude = Array.Empty<string>(),
                },
            },
            rules = new[]
            {
                new
                {
                    type = "required_status_checks",
                    parameters = new
                    {
                        required_status_checks = new[]
                        {
                            new
                            {
                                context = CheckRunConstants.ZeeqCheckRunName,
                                integration_id = appId,
                            },
                        },
                        strict_required_status_checks_policy = false,
                        do_not_enforce_on_create = true,
                    },
                },
            },
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(ruleset);

        return TypedResults.File(
            bytes,
            "application/json",
            "zeeq-code-review-ruleset.json"
        );
    }
}
