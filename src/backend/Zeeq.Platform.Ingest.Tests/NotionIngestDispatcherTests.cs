using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Core.Documents.Dispatch;
using Zeeq.Core.Llm;
using Zeeq.Integrations.Notion;

namespace Zeeq.Platform.Ingest.Tests;

public sealed class NotionIngestDispatcherTests
{
    [Test]
    public async Task RunAsync_MissingEncryptedToken_RecordsAuthFailure()
    {
        var libraries = new FakeLibraryDocumentStore();
        libraries.Libraries.Add(
            new Library
            {
                Id = "library-1",
                OrganizationId = "org-1",
                Name = "Notion",
                SourceKind = RepositorySourceKind.Notion.ToString(),
                ExternalSource = new LibraryExternalSource
                {
                    Notion = new NotionSourceConfiguration
                    {
                        AccessTokenValueId = "enc-missing",
                        CallbackTokenSerial = 1,
                    },
                },
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            }
        );
        var encryptedValues = Substitute.For<IEncryptedValueStore>();
        var pending = Substitute.For<IExternalPendingContentSyncStore>();
        var runs = new FakeDocsIngestRunStore();
        var runner = new NotionIngestRunner(
            libraries,
            pending,
            runs,
            new IngestSettings(),
            NullLogger<NotionIngestRunner>.Instance
        );
        var dispatcher = new NotionIngestDispatcher(
            libraries,
            encryptedValues,
            new EncryptedValueEncryptionService(new LlmSettings(), []),
            Substitute.For<IZeeqNotionClientFactory>(),
            runner,
            runs,
            NullLogger<NotionIngestDispatcher>.Instance
        );
        var job = Job();

        var outcome = await dispatcher.RunAsync(job, default);
        var run = await runs.GetAsync(job.RunId, job.RunCreatedAtUtc, default);

        await Assert.That(outcome.Status).IsEqualTo(IngestRunStatus.Failed);
        await Assert.That(run).IsNotNull();
        await Assert.That(run!.Status).IsEqualTo(IngestRunStatus.Failed);
        await Assert.That(run.AuthFailure).IsTrue();
    }

    private static NotionIngestJob Job() =>
        new()
        {
            RunId = "run-1",
            RunCreatedAtUtc = new DateTimeOffset(2026, 8, 8, 12, 0, 0, TimeSpan.Zero),
            OrganizationId = "org-1",
            LibraryId = "library-1",
            Scope = ExternalSyncScope.Incremental,
            Trigger = IngestTriggerReason.Scheduled,
            Filter = EffectiveFilter.Empty,
            SourceReference = "notion://library/library-1",
            TraceContext = new ZeeqTraceContext(null, null),
        };
}
