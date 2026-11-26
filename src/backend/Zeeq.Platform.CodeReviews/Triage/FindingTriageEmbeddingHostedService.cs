using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zeeq.Core.Common;
using Zeeq.Core.Llm;
using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>Embeds confirmed false positives using fenced, expiring claims.</summary>
public sealed class FindingTriageEmbeddingHostedService(
    IServiceScopeFactory scopes,
    LlmEmbeddingSettings settings,
    ILogger<FindingTriageEmbeddingHostedService> logger
) : BackgroundService
{
    private static readonly System.Diagnostics.Metrics.Counter<long> Writes =
        ZeeqTelemetry.Metrics.CreateCounter<long>("zeeq_code_review_triage_embedding_writes_total");

    /// <summary>Periodically processes a bounded batch of outstanding claims.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            if (settings.Enabled)
            {
                try
                {
                    await ProcessBatchAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Finding triage embedding batch failed");
                }
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task ProcessBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ICodeReviewFindingTriageStore>();
        var model = $"{settings.Model}@{settings.Dimensions}";
        var claims = await store.ClaimPendingEmbeddingsAsync(
            model,
            TimeSpan.FromMinutes(10),
            32,
            cancellationToken
        );
        if (claims.Count == 0)
            return;
        using var activity = ZeeqTelemetry.Trace(
            [("batch.size", claims.Count), ("embedding.model", model)],
            "code_reviews.triage.embed_batch"
        );
        var written = 0;
        var stale = 0;
        try
        {
            var generator = scope.ServiceProvider.GetRequiredKeyedService<
                IEmbeddingGenerator<string, Embedding<float>>
            >(DefaultLlmChatClientKeys.SnippetEmbeddingsBatch);
            var vectors = await generator.GenerateAsync(
                claims.Select(x =>
                    $"{x.Row.Facet}\n{x.Row.FindingSummary}\nReason: {x.Row.CurrentReason}\nLabel: {x.Row.SemanticId}"
                ),
                new EmbeddingGenerationOptions { Dimensions = settings.Dimensions },
                cancellationToken
            );
            if (vectors.Count != claims.Count)
                throw new InvalidOperationException("Embedding count mismatch.");
            for (var i = 0; i < claims.Count; i++)
            {
                var claim = claims[i];
                var vector = EmbeddingVectorValidation.ToHalfVector(
                    vectors[i],
                    settings.Dimensions
                );
                if (vector is null)
                {
                    // Leave the lease in place until expiry to avoid retrying bad provider output every minute.
                    logger.LogWarning(
                        "Invalid embedding for finding triage {TriageId}",
                        claim.Row.Id
                    );
                    Writes.Add(1, new KeyValuePair<string, object?>("result", "failed"));
                    continue;
                }
                var updated = await store.TryCompleteEmbeddingAsync(
                    claim.Row.OrganizationId,
                    claim.Row.Id,
                    claim.UpdatedAtUtc,
                    claim.StartedAtUtc,
                    vector,
                    model,
                    cancellationToken
                );
                if (updated)
                    written++;
                else
                    stale++;
                Writes.Add(
                    1,
                    new KeyValuePair<string, object?>(
                        "result",
                        updated ? "updated" : "stale_dropped"
                    )
                );
            }
        }
        catch
        {
            Writes.Add(1, new KeyValuePair<string, object?>("result", "failed"));
            foreach (var claim in claims)
            {
                try
                {
                    var released = await store.ReleaseEmbeddingClaimAsync(
                        claim.Row.OrganizationId,
                        claim.Row.Id,
                        claim.UpdatedAtUtc,
                        claim.StartedAtUtc,
                        CancellationToken.None
                    );
                    Writes.Add(
                        1,
                        new KeyValuePair<string, object?>(
                            "result",
                            released ? "released" : "stale_dropped"
                        )
                    );
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not release finding triage embedding claim");
                }
            }
            throw;
        }
        finally
        {
            activity?.SetTag("written.count", written);
            activity?.SetTag("stale_dropped.count", stale);
        }
    }
}
