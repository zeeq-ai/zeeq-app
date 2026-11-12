using Npgsql;
using Zeeq.Core.Common;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Runtime.Server.Setup;

/// <summary>Explicit local-only composition for deterministic browser acceptance.</summary>
internal static class SetupCodeReviewEvaluationFixtures
{
    public static void AddCodeReviewEvaluationFixtures(
        this WebApplicationBuilder builder,
        AppSettings settings
    )
    {
        // Production never registers fixture adapters, even if a stray configuration key exists.
        if (!builder.Environment.IsDevelopment())
            return;
        var runId = builder.Configuration["Development:CodeReviewEvaluationFixtureRunId"];
        if (string.IsNullOrWhiteSpace(runId))
            return;
        if (!runId.All(char.IsAsciiLetterOrDigit))
            throw new InvalidOperationException(
                "The evaluation fixture run id must be alphanumeric."
            );
        var connection = new NpgsqlConnectionStringBuilder(
            settings.Database.EffectiveConnectionString
        );
        if (
            connection.Host is not ("localhost" or "127.0.0.1" or "::1")
            || connection.Database != "zeeq"
        )
            throw new InvalidOperationException(
                "Evaluation fixtures require the local development database."
            );
        builder.Services.AddSingleton(
            new CodeReviewEvaluationFixtureRepositories(
                builder.Environment,
                [
                    ("org_0factor_e2e_alpha", $"repo_bt_{runId}_alpha"),
                    ("org_0factor_e2e_beta", $"repo_bt_{runId}_beta"),
                ]
            )
        );
        Zeeq.Data.Postgres.CodeReviews.SetupCodeReviewEvaluationFixtures.AddZeeqCodeReviewEvaluationFixtures(
            builder.Services
        );
        builder.Services.AddScoped<CodeReviewEvaluationJudge>();
        builder.Services.AddScoped<ICodeReviewEvaluationJudge, CodeReviewEvaluationFixtureJudge>();
        builder.Services.AddScoped<ICodeReviewAgentExecutor, CodeReviewEvaluationFixtureReviewer>();
    }
}
