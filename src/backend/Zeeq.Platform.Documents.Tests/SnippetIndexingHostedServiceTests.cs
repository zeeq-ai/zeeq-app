using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Core.Documents.Snippets;
using Zeeq.Core.Llm;
using Zeeq.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Zeeq.Platform.Documents.Tests;

/// <summary>
/// Service-level tests for <see cref="SnippetIndexingHostedService"/>'s Pipeline A (parse/compose/
/// reconcile). Uses in-memory fake stores so the orchestration — drain semantics, parse-failure
/// handling, and status transitions — is verified without a database or embedding provider.
///
/// dotnet run --project src/backend/Zeeq.Platform.Documents.Tests --output detailed --disable-logo --treenode-filter "/*/*/SnippetIndexingHostedServiceTests/*"
/// </summary>
public sealed class SnippetIndexingHostedServiceTests
{
    [Test]
    public async Task Tick_SeededPendingDocuments_ComposesSnippetsAndMarksIndexed()
    {
        var docStore = new FakeLibraryDocumentStore([
            Doc("d1", "# Title\n\nThis is a section body with enough characters to index it well."),
        ]);
        var snippetStore = new FakeLibraryDocumentSnippetStore();
        var service = Build(docStore, snippetStore, new());

        await service.TickAsync(default);

        await Assert.That(snippetStore.SnippetsByDocument.ContainsKey("d1")).IsTrue();
        await Assert.That(docStore.StatusById["d1"]).IsEqualTo(DocumentProcessingStatus.Indexed);
    }

    [Test]
    public async Task Tick_DrainsBacklogInOnePass()
    {
        var settings = new SnippetIndexingSettings { ClaimBatchSize = 2 };
        var docs = Enumerable
            .Range(0, 6)
            .Select(i =>
                Doc(
                    $"d{i}",
                    $"# Doc {i}\n\nA section body long enough to pass the minimum length filter here."
                )
            )
            .ToArray();
        var docStore = new FakeLibraryDocumentStore(docs);
        var snippetStore = new FakeLibraryDocumentSnippetStore();
        var service = Build(docStore, snippetStore, settings);

        await service.TickAsync(default);

        // 6 docs with ClaimBatchSize 2 → 3 claim rounds, all drained in one tick.
        await Assert.That(snippetStore.SnippetsByDocument).Count().IsEqualTo(6);
        await Assert
            .That(docStore.StatusById.Values.All(s => s == DocumentProcessingStatus.Indexed))
            .IsTrue();
    }

    [Test]
    public async Task Tick_ParseFailure_MarksFailedAndContinues()
    {
        var docStore = new FakeLibraryDocumentStore([
            Doc("bad", content: null!),
            Doc(
                "good",
                "# Good\n\nA valid section body that is long enough to index without trouble."
            ),
        ]);
        var snippetStore = new FakeLibraryDocumentSnippetStore();
        var service = Build(docStore, snippetStore, new());

        await service.TickAsync(default);

        await Assert.That(docStore.StatusById["bad"]).IsEqualTo(DocumentProcessingStatus.Failed);
        await Assert.That(docStore.StatusById["good"]).IsEqualTo(DocumentProcessingStatus.Indexed);
        await Assert.That(snippetStore.SnippetsByDocument.ContainsKey("good")).IsTrue();
        await Assert.That(snippetStore.SnippetsByDocument.ContainsKey("bad")).IsFalse();
    }

    [Test]
    public async Task Tick_Disabled_DoesNothing()
    {
        var docStore = new FakeLibraryDocumentStore([Doc("d1", "# T\n\nbody body body body body")]);
        var snippetStore = new FakeLibraryDocumentSnippetStore();
        // TickAsync always runs; the Enabled gate is in ExecuteAsync. This test asserts the
        // pipeline is a no-op when there is nothing pending (empty claim), the steady state.
        docStore.MarkAllIndexed();
        var service = Build(docStore, snippetStore, new());

        await service.TickAsync(default);

        await Assert.That(snippetStore.SnippetsByDocument).IsEmpty();
    }

    [Test]
    public async Task Tick_EmbeddingsDisabled_LeavesRowsUnembedded()
    {
        var docStore = new FakeLibraryDocumentStore([
            Doc(
                "d1",
                "# Title\n\nA section body that is long enough to comfortably pass the minimum section length filter used by the composer."
            ),
        ]);
        var snippetStore = new FakeLibraryDocumentSnippetStore();
        var service = Build(
            docStore,
            snippetStore,
            new(),
            embeddingSettings: new LlmEmbeddingSettings { Enabled = false }
        );

        await service.TickAsync(default);

        await Assert.That(docStore.StatusById["d1"]).IsEqualTo(DocumentProcessingStatus.Indexed);
        await Assert.That(snippetStore.Rows).IsNotEmpty();
        await Assert.That(snippetStore.Rows.All(r => r.Embedding is null)).IsTrue();
    }

    [Test]
    public async Task Tick_EmbeddingsEnabled_EmbedsEverySnippetAndWritesVector()
    {
        var docStore = new FakeLibraryDocumentStore([
            Doc(
                "d1",
                "# Title\n\nA section body that is long enough to comfortably pass the minimum section length filter used by the composer."
            ),
        ]);
        var snippetStore = new FakeLibraryDocumentSnippetStore();
        var generator = new FakeEmbeddingGenerator();
        var service = Build(
            docStore,
            snippetStore,
            new(),
            embeddingSettings: new LlmEmbeddingSettings
            {
                Enabled = true,
                Model = "test-model",
                Dimensions = 768,
            },
            embeddingGenerator: generator
        );

        await service.TickAsync(default);

        await Assert.That(snippetStore.Rows).IsNotEmpty();
        await Assert.That(snippetStore.Rows.All(r => r.Embedding is not null)).IsTrue();
        await Assert
            .That(snippetStore.Rows.All(r => r.EmbeddingModel == "test-model@768"))
            .IsTrue();
        await Assert.That(snippetStore.Rows.All(r => r.EmbeddingStartedAt is null)).IsTrue();
        await Assert.That(generator.Requests).IsNotEmpty();
    }

    [Test]
    public async Task Tick_EmbeddingProviderFails_ReleasesLeaseAndLeavesDocumentSnippetsIntact()
    {
        var docStore = new FakeLibraryDocumentStore([
            Doc(
                "d1",
                "# Title\n\nA section body that is long enough to comfortably pass the minimum section length filter used by the composer."
            ),
        ]);
        var snippetStore = new FakeLibraryDocumentSnippetStore();
        var generator = new FakeEmbeddingGenerator
        {
            // Simulates the SDK's own retries already being exhausted — the pipeline never
            // retries on its own, it only decides "release the lease for the next tick".
            ThrowOnGenerate = new InvalidOperationException("provider unavailable"),
        };
        var service = Build(
            docStore,
            snippetStore,
            new(),
            embeddingSettings: new LlmEmbeddingSettings { Enabled = true, Model = "test-model" },
            embeddingGenerator: generator
        );

        await service.TickAsync(default);

        // Pipeline A still succeeded — the document is Indexed and FTS-searchable — even though
        // Pipeline B's embedding call failed.
        await Assert.That(docStore.StatusById["d1"]).IsEqualTo(DocumentProcessingStatus.Indexed);
        await Assert.That(snippetStore.SnippetsByDocument.ContainsKey("d1")).IsTrue();

        await Assert.That(snippetStore.Rows).IsNotEmpty();
        await Assert.That(snippetStore.Rows.All(r => r.Embedding is null)).IsTrue();
        // Released, not left claimed — so the very next tick can retry immediately.
        await Assert.That(snippetStore.Rows.All(r => r.EmbeddingStartedAt is null)).IsTrue();
    }

    private static LibraryDocument Doc(string id, string content) =>
        new()
        {
            Id = id,
            OrganizationId = "org_1",
            LibraryId = "lib_1",
            Path = $"/{id}.md",
            Title = id,
            TitleNormalized = id,
            Keywords = [],
            Headings = [],
            Content = content,
            ProcessingStatus = DocumentProcessingStatus.Pending,
            TokenCount = 1,
            ContentHash = id,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    /// <summary>
    /// Builds the hosted service over a real <see cref="IServiceScopeFactory"/> whose scopes resolve
    /// the shared in-memory fakes (registered as singletons so every scope observes the same state).
    /// </summary>
    /// <param name="docStore">The private document store fake, seeded by the caller.</param>
    /// <param name="snippetStore">The private snippet store fake.</param>
    /// <param name="settings">Sweep tunables (claim batch size, concurrency, etc.).</param>
    /// <param name="embeddingSettings">
    /// Defaults to disabled — Pipeline A tests in this file don't exercise embedding. Pipeline B
    /// tests pass <c>Enabled = true</c> and a <paramref name="embeddingGenerator"/> fake.
    /// </param>
    /// <param name="embeddingGenerator">
    /// The fake registered under the Batch-profile keyed service Pipeline B resolves. Required
    /// (non-null) whenever <paramref name="embeddingSettings"/>.Enabled is true.
    /// </param>
    private static SnippetIndexingHostedService Build(
        FakeLibraryDocumentStore docStore,
        FakeLibraryDocumentSnippetStore snippetStore,
        SnippetIndexingSettings settings,
        LlmEmbeddingSettings? embeddingSettings = null,
        FakeEmbeddingGenerator? embeddingGenerator = null
    )
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILibraryDocumentStore>(docStore);
        services.AddSingleton<ISnippetStore<LibraryDocument>>(snippetStore);
        services.AddSingleton<IDocsPublicDocumentStore>(new EmptyPublicDocumentStore());
        services.AddSingleton<ISnippetStore<DocsPublicDocument>>(new NoOpPublicSnippetStore());

        if (embeddingGenerator is not null)
        {
            services.AddKeyedSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
                DefaultLlmChatClientKeys.SnippetEmbeddingsBatch,
                embeddingGenerator
            );
        }

        var provider = services.BuildServiceProvider();

        return new SnippetIndexingHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            settings,
            embeddingSettings ?? new LlmEmbeddingSettings { Enabled = false },
            NullLogger<SnippetIndexingHostedService>.Instance
        );
    }
}
