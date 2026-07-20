using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Zeeq.Platform.Ingest.Tests;

/// <summary>
/// Tests for <see cref="IngestSchedulerHostedService"/>.
///
/// dotnet run --project src/backend/Zeeq.Platform.Ingest.Tests --output detailed --disable-logo --treenode-filter "/*/*/IngestSchedulerHostedServiceTests/*"
/// </summary>
public sealed class IngestSchedulerHostedServiceTests
{
    private static readonly IngestSettings Settings = new() { SchedulerBatchSize = 10 };

    private static DocsPublicSource Source(string id, string syncStatus = "queued")
    {
        var runCreatedAtUtc = DateTimeOffset.UtcNow;
        return new()
        {
            Id = id,
            Kind = RepositorySourceKind.Public,
            RepoUrl = $"https://github.com/example/{id}",
            Name = id,
            SyncStatus = syncStatus,
            ActiveSyncRunId = $"run_{id}",
            ActiveSyncRunCreatedAtUtc = runCreatedAtUtc,
            SyncQueuedAtUtc = runCreatedAtUtc,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static Library PrivateLibrary(
        string id,
        string organizationId = "org_1",
        string syncStatus = "queued"
    )
    {
        var runCreatedAtUtc = DateTimeOffset.UtcNow;
        return new()
        {
            Id = id,
            OrganizationId = organizationId,
            Name = id,
            SourceKind = "GitHub",
            SourceRepoUrl = $"https://github.com/example/{id}",
            SyncStatus = syncStatus,
            ActiveSyncRunId = $"run_{id}",
            ActiveSyncRunCreatedAtUtc = runCreatedAtUtc,
            SyncQueuedAtUtc = runCreatedAtUtc,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static (
        IngestSchedulerHostedService Service,
        ClaimStubDocsPublicSourceStore Sources,
        ClaimStubLibraryDocumentStore Libraries,
        TestMessagePublisher Publisher
    ) Build(
        IReadOnlyList<DocsPublicSource>? dueSources = null,
        IReadOnlyList<Library>? dueLibraries = null,
        IngestSettings? settings = null
    )
    {
        var sources = new ClaimStubDocsPublicSourceStore(dueSources ?? []);
        var libraries = new ClaimStubLibraryDocumentStore(dueLibraries ?? []);
        var publisher = new TestMessagePublisher();

        var services = new ServiceCollection();
        services.AddScoped<IDocsPublicSourceStore>(_ => sources);
        services.AddScoped<ILibraryDocumentStore>(_ => libraries);
        services.AddScoped<Zeeq.Platform.Messaging.IZeeqMessagePublisher>(_ => publisher);
        var provider = services.BuildServiceProvider();

        var service = new IngestSchedulerHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            settings ?? Settings,
            NullLogger<IngestSchedulerHostedService>.Instance
        );

        return (service, sources, libraries, publisher);
    }

    [Test]
    public async Task TickAsync_NothingDue_PublishesNothing()
    {
        var (service, _, _, publisher) = Build();

        await service.TickAsync(CancellationToken.None);

        await Assert.That(publisher.Published).IsEmpty();
    }

    [Test]
    public async Task TickAsync_PublicSourcesDue_PublishesOneMessagePerSource()
    {
        var (service, _, _, publisher) = Build(
            dueSources: [Source("src_1"), Source("src_2"), Source("src_3")]
        );

        await service.TickAsync(CancellationToken.None);

        var published = publisher.Published.OfType<PublicRepositorySyncRequested>().ToList();
        await Assert.That(published.Count).IsEqualTo(3);
        await Assert
            .That(published.Select(m => m.PublicSourceId))
            .IsEquivalentTo(["src_1", "src_2", "src_3"]);
        await Assert
            .That(published.Select(m => m.Trigger))
            .IsEquivalentTo([
                IngestTriggerReason.Scheduled,
                IngestTriggerReason.Scheduled,
                IngestTriggerReason.Scheduled,
            ]);
    }

    [Test]
    public async Task TickAsync_EachPublishedPublicMessage_HasUniqueRunId()
    {
        var (service, _, _, publisher) = Build(dueSources: [Source("src_1"), Source("src_2")]);

        await service.TickAsync(CancellationToken.None);

        var runIds = publisher
            .Published.OfType<PublicRepositorySyncRequested>()
            .Select(m => m.RunId)
            .ToList();
        await Assert.That(runIds.Distinct().Count()).IsEqualTo(runIds.Count);
        await Assert.That(runIds).All().Satisfy(id => id!.StartsWith("run_"));
    }

    [Test]
    public async Task TickAsync_PassesConfiguredBatchSizeToPublicClaim()
    {
        var (service, sources, _, _) = Build(settings: new IngestSettings { SchedulerBatchSize = 7 });

        await service.TickAsync(CancellationToken.None);

        await Assert.That(sources.LastClaimLimit).IsEqualTo(7);
    }

    [Test]
    public async Task TickAsync_PrivateLibrariesDue_PublishesOneMessagePerLibrary()
    {
        var (service, _, _, publisher) = Build(
            dueLibraries: [PrivateLibrary("lib_1"), PrivateLibrary("lib_2")]
        );

        await service.TickAsync(CancellationToken.None);

        var published = publisher.Published.OfType<PrivateRepositorySyncRequested>().ToList();
        await Assert.That(published.Count).IsEqualTo(2);
        await Assert
            .That(published.Select(m => m.LibraryId))
            .IsEquivalentTo(["lib_1", "lib_2"]);
        await Assert
            .That(published.Select(m => m.OrganizationId))
            .IsEquivalentTo(["org_1", "org_1"]);
        await Assert
            .That(published.Select(m => m.Trigger))
            .IsEquivalentTo([IngestTriggerReason.Scheduled, IngestTriggerReason.Scheduled]);
    }

    [Test]
    public async Task TickAsync_EachPublishedPrivateMessage_HasUniqueRunId()
    {
        var (service, _, _, publisher) = Build(
            dueLibraries: [PrivateLibrary("lib_1"), PrivateLibrary("lib_2")]
        );

        await service.TickAsync(CancellationToken.None);

        var runIds = publisher
            .Published.OfType<PrivateRepositorySyncRequested>()
            .Select(m => m.RunId)
            .ToList();
        await Assert.That(runIds.Distinct().Count()).IsEqualTo(runIds.Count);
        await Assert.That(runIds).All().Satisfy(id => id!.StartsWith("run_"));
    }

    [Test]
    public async Task TickAsync_PassesConfiguredBatchSizeToPrivateClaim()
    {
        var (service, _, libraries, _) = Build(
            settings: new IngestSettings { SchedulerBatchSize = 7 }
        );

        await service.TickAsync(CancellationToken.None);

        await Assert.That(libraries.LastClaimLimit).IsEqualTo(7);
    }

    [Test]
    public async Task TickAsync_BothKindsDue_PublishesBoth()
    {
        var (service, _, _, publisher) = Build(
            dueSources: [Source("src_1")],
            dueLibraries: [PrivateLibrary("lib_1")]
        );

        await service.TickAsync(CancellationToken.None);

        await Assert.That(publisher.Published.OfType<PublicRepositorySyncRequested>().Count()).IsEqualTo(1);
        await Assert.That(publisher.Published.OfType<PrivateRepositorySyncRequested>().Count()).IsEqualTo(1);
    }

    /// <summary>
    /// In-memory <see cref="IDocsPublicSourceStore"/> that only supports the
    /// claim call the scheduler makes, recording the limit it was called
    /// with.
    /// </summary>
    private sealed class ClaimStubDocsPublicSourceStore(IReadOnlyList<DocsPublicSource> dueSources)
        : IDocsPublicSourceStore
    {
        public int? LastClaimLimit { get; private set; }

        public Task<IReadOnlyList<DocsPublicSource>> ClaimDueForSyncAsync(
            int limit,
            CancellationToken ct
        )
        {
            LastClaimLimit = limit;
            return Task.FromResult(dueSources);
        }

        public Task<DocsPublicSource?> GetByIdAsync(string id, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<DocsPublicSource?> GetByRepoUrlAsync(string repoUrl, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<DocsPublicSource>> GetByIdsAsync(
            IReadOnlyCollection<string> ids,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<DocsPublicSource> CreateAsync(DocsPublicSource source, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<DocsPublicSource> UpdateAsync(DocsPublicSource source, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// In-memory <see cref="ILibraryDocumentStore"/> that only supports the
    /// claim call the scheduler makes, recording the limit it was called
    /// with.
    /// </summary>
    private sealed class ClaimStubLibraryDocumentStore(IReadOnlyList<Library> dueLibraries)
        : ILibraryDocumentStore
    {
        public int? LastClaimLimit { get; private set; }

        public Task<IReadOnlyList<Library>> ClaimDueForSyncAsync(int limit, CancellationToken ct)
        {
            LastClaimLimit = limit;
            return Task.FromResult(dueLibraries);
        }

        public Task<IReadOnlyList<LibraryDocument>> ClaimPendingIndexingAsync(
            int limit,
            TimeSpan staleAfter,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task SetProcessingStatusAsync(
            LibraryDocument document,
            DocumentProcessingStatus status,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<Library?> GetLibraryAsync(
            string organizationId,
            string name,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<Library?> GetLibraryByIdAsync(
            string organizationId,
            string libraryId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<Library>> ListLibrariesAsync(
            string organizationId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<Library>> ListLibrariesByPublicSourceIdAsync(
            string publicSourceId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<Library> CreateLibraryAsync(Library library, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Library> UpdateLibraryAsync(Library library, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task DeleteLibraryAsync(
            string organizationId,
            string libraryId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<Library> UpdateSyncStateAsync(
            string organizationId,
            string libraryId,
            string? syncStatus,
            DateTimeOffset? nextSyncAt,
            DateTimeOffset[] manualTriggerHistory,
            DateTimeOffset? sourceSyncedAt,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<LibraryDocument> UpsertDocumentAsync(
            LibraryDocument document,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<LibraryDocumentUpsertResult> UpsertSyncedDocumentAsync(
            LibraryDocument document,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<int> DeleteUnstampedAsync(
            string organizationId,
            string libraryId,
            string currentSyncRunId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task DeleteDocumentAsync(
            string organizationId,
            string libraryId,
            string path,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<LibraryDocument?> GetByPathAsync(
            string organizationId,
            string libraryId,
            string input,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<LibraryDocumentMatch>> SearchAsync(
            string organizationId,
            string libraryId,
            string query,
            int limit,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<LibraryDocument>> ListDocumentsAsync(
            string organizationId,
            string libraryId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<LibraryDocument?> MoveDocumentAsync(
            string organizationId,
            string libraryId,
            string fromPath,
            string toPath,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<LibraryDocument?> SetCodeReviewExclusionAsync(
            string organizationId,
            string libraryId,
            string path,
            bool excluded,
            CancellationToken ct
        ) => throw new NotSupportedException();
    }
}
