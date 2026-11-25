using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pgvector;
using Zeeq.Core.Common;
using Zeeq.Core.Llm;
using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews.Tests;

public class KnownFalsePositiveRetrievalTests
{
    [Test]
    public async Task VectorValidationRejectsZeroNonfiniteAndHalfOverflow()
    {
        await Assert
            .That(EmbeddingVectorValidation.ToHalfVector(new Embedding<float>(new float[768]), 768))
            .IsNull();
        var values = Enumerable.Repeat(1f, 768).ToArray();
        values[0] = float.NaN;
        await Assert
            .That(EmbeddingVectorValidation.ToHalfVector(new Embedding<float>(values), 768))
            .IsNull();
        values[0] = 1e10f;
        await Assert
            .That(EmbeddingVectorValidation.ToHalfVector(new Embedding<float>(values), 768))
            .IsNull();
        values[0] = 1f;
        await Assert
            .That(EmbeddingVectorValidation.ToHalfVector(new Embedding<float>(values), 768))
            .IsNotNull();
    }

    [Test]
    public async Task RetrievalFailsOpenOnProviderFailure()
    {
        var store = Substitute.For<ICodeReviewFindingTriageStore>();
        var services = new ServiceCollection()
            .AddKeyedSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
                DefaultLlmChatClientKeys.SnippetEmbeddingsQuery,
                new ThrowingGenerator()
            )
            .BuildServiceProvider();
        var service = new KnownFalsePositiveRetrievalService(
            store,
            services,
            new LlmEmbeddingSettings { Enabled = true },
            NullLogger<KnownFalsePositiveRetrievalService>.Instance
        );
        var result = await service.RetrieveAsync(
            "org",
            "repo",
            "owner/repo",
            [new("a.cs", null, CodeReviewFileMutationState.Modified, "@@ patch")],
            1,
            null,
            "review",
            CancellationToken.None
        );
        await Assert.That(result).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task RankingPrefersExactThenDirectoryPaths()
    {
        var row = new CodeReviewFindingTriage
        {
            Id = "id",
            OrganizationId = "org",
            OwnerQualifiedRepoName = "o/r",
            SourceReviewId = "review",
            SourceReviewCreatedAtUtc = DateTimeOffset.UtcNow,
            OccurrenceKey = "F1",
            SemanticId = "test-finding-id",
            Facet = "security",
            FindingSummary = "summary",
            FilePath = "src/foo.cs",
        };
        var match = new FindingTriageMatch(row, 0.2);
        await Assert
            .That(KnownFalsePositiveRetrievalService.Score(match, "src/foo.cs"))
            .IsLessThan(KnownFalsePositiveRetrievalService.Score(match, "src/bar.cs"));
        await Assert
            .That(KnownFalsePositiveRetrievalService.Score(match, "src/bar.cs"))
            .IsLessThan(KnownFalsePositiveRetrievalService.Score(match, "other/bar.cs"));
    }

    [Test]
    public async Task WorkerReleasesExactLeaseWhenProviderThrows()
    {
        var store = Substitute.For<ICodeReviewFindingTriageStore>();
        var now = DateTimeOffset.UtcNow;
        var row = new CodeReviewFindingTriage
        {
            Id = "id",
            OrganizationId = "org",
            OwnerQualifiedRepoName = "o/r",
            SourceReviewId = "review",
            SourceReviewCreatedAtUtc = now,
            OccurrenceKey = "F1",
            SemanticId = "test-finding-id",
            Facet = "security",
            FindingSummary = "summary",
        };
        var claim = new FindingTriageEmbeddingClaim(row, now.AddMinutes(-1), now);
        store
            .ClaimPendingEmbeddingsAsync(
                Arg.Any<string>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(new[] { claim });
        var services = new ServiceCollection()
            .AddSingleton(store)
            .AddKeyedSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
                DefaultLlmChatClientKeys.SnippetEmbeddingsBatch,
                new ThrowingGenerator()
            )
            .BuildServiceProvider();
        var worker = new FindingTriageEmbeddingHostedService(
            services.GetRequiredService<IServiceScopeFactory>(),
            new LlmEmbeddingSettings { Enabled = true },
            NullLogger<FindingTriageEmbeddingHostedService>.Instance
        );
        await Assert
            .That(async () => await worker.ProcessBatchAsync(CancellationToken.None))
            .Throws<InvalidOperationException>();
        await store
            .Received(1)
            .ReleaseEmbeddingClaimAsync(
                "org",
                "id",
                claim.UpdatedAtUtc,
                claim.StartedAtUtc,
                Arg.Any<CancellationToken>()
            );
    }

    [Test]
    public async Task RetrievalBatchesFilesFiltersUnrelatedClaimsAndEscapesXml()
    {
        var store = Substitute.For<ICodeReviewFindingTriageStore>();
        var now = DateTimeOffset.UtcNow;
        CodeReviewFindingTriage Row(string id, string path) =>
            new()
            {
                Id = id,
                OrganizationId = "org",
                OwnerQualifiedRepoName = "o/r",
                SourceReviewId = "review",
                SourceReviewCreatedAtUtc = now,
                OccurrenceKey = "F1",
                SemanticId = "test-finding-id",
                Facet = "security",
                FindingSummary = "<script>",
                CurrentReason = "do & check",
                FilePath = path,
            };
        store
            .SearchConfirmedFalsePositivesAsync(
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string>(),
                Arg.Any<HalfVector>(),
                Arg.Any<string>(),
                Arg.Any<double>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<int?>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(
                new[]
                {
                    new FindingTriageMatch(Row("one", "src/a.cs"), 0.1),
                    new FindingTriageMatch(Row("two", "unrelated.cs"), 0.01),
                }
            );
        var generator = new ValidGenerator();
        var services = new ServiceCollection()
            .AddKeyedSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
                DefaultLlmChatClientKeys.SnippetEmbeddingsQuery,
                generator
            )
            .BuildServiceProvider();
        var service = new KnownFalsePositiveRetrievalService(
            store,
            services,
            new LlmEmbeddingSettings { Enabled = true },
            NullLogger<KnownFalsePositiveRetrievalService>.Instance
        );
        var xml = await service.RetrieveAsync(
            "org",
            "repo",
            "Owner/Repo",
            [
                new("src/a.cs", null, CodeReviewFileMutationState.Modified, "patch"),
                new("src/b.cs", null, CodeReviewFileMutationState.Modified, "patch"),
            ],
            42,
            null,
            "review",
            CancellationToken.None
        );
        await Assert.That(generator.Calls).IsEqualTo(1);
        await Assert.That(xml).Contains("&lt;script&gt;");
        await Assert.That(xml).Contains("do &amp; check");
        await Assert.That(xml).DoesNotContain("unrelated.cs");
        await store
            .Received(2)
            .SearchConfirmedFalsePositivesAsync(
                "org",
                "repo",
                "owner/repo",
                Arg.Any<HalfVector>(),
                Arg.Any<string>(),
                0.75,
                32,
                42,
                null,
                "review",
                null,
                null,
                Arg.Any<CancellationToken>()
            );
    }

    private sealed class ValidGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public int Calls { get; private set; }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            Calls++;
            return Task.FromResult(
                new GeneratedEmbeddings<Embedding<float>>(
                    values.Select(_ => new Embedding<float>(Enumerable.Repeat(1f, 768).ToArray()))
                )
            );
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class ThrowingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException("Unavailable");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
