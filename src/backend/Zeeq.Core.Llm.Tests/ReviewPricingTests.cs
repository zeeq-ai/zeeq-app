using Microsoft.Extensions.AI;

namespace Zeeq.Core.Llm.Tests;

/// <summary>Review estimates use the shared catalog and the explicit zero-cost policy.</summary>
public sealed class ReviewPricingTests
{
    [Test]
    public async Task EstimateReviewCost_PricesFreshCachedAndOutputTokens()
    {
        var usage = new LlmUsageSink();
        usage.Add(
            new UsageDetails
            {
                InputTokenCount = 1_000,
                CachedInputTokenCount = 200,
                OutputTokenCount = 100,
            }
        );

        await Assert
            .That(PricingCatalog.EstimateReviewCost("gpt-6.1-sol", usage))
            .IsEqualTo(0.00262m);
    }

    [Test]
    public async Task EstimateReviewCost_DeepSeekAndGlmAreZeroWithoutUsage()
    {
        var usage = new LlmUsageSink();

        await Assert
            .That(
                PricingCatalog.EstimateReviewCost(
                    "accounts/fireworks/models/deepseek-v4-pro",
                    usage
                )
            )
            .IsEqualTo(0m);
        await Assert
            .That(PricingCatalog.EstimateReviewCost("accounts/fireworks/models/glm-5p2", usage))
            .IsEqualTo(0m);
    }

    [Test]
    public async Task EstimateReviewCost_UnknownOrIncompleteUsageRemainsUnknown()
    {
        var usage = new LlmUsageSink();
        usage.Add(new UsageDetails { InputTokenCount = 500 });

        await Assert.That(PricingCatalog.EstimateReviewCost("gpt-6.1-sol", usage)).IsNull();
        await Assert.That(PricingCatalog.EstimateReviewCost("unlisted-model", usage)).IsNull();
    }
}
