using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Integrations.Notion;
using Zeeq.Platform.Messaging;

namespace Zeeq.Platform.Ingest.Tests;

public sealed class NotionPageChangeWebhookReceivedHandlerTests
{
    private readonly INotionWebhookStore _webhookState = Substitute.For<INotionWebhookStore>();
    private readonly ILibraryDocumentStore _libraries = Substitute.For<ILibraryDocumentStore>();
    private readonly IExternalPendingContentSyncStore _pending =
        Substitute.For<IExternalPendingContentSyncStore>();

    [Arguments("page.created", true, false)]
    [Arguments("page.undeleted", true, false)]
    [Arguments("page.content_updated", true, false)]
    [Arguments("page.properties_updated", true, false)]
    [Arguments("page.moved", true, false)]
    [Arguments("page.deleted", false, true)]
    [Arguments("page.locked", false, false)]
    [Arguments("page.unlocked", false, false)]
    [Test]
    public async Task HandleAsync_RoutesPageEventMatrix(
        string eventType,
        bool expectedDirty,
        bool expectedDelete
    )
    {
        ArrangeCurrentWebhook();
        var message = Message(eventType);

        await Handler().HandleAsync(message, CancellationToken.None);

        if (expectedDirty)
        {
            await _pending
                .Received(1)
                .UpsertAsync(
                    "org-1",
                    "library-1",
                    "page-1",
                    eventType,
                    message.EventTimestamp!.Value,
                    Arg.Any<CancellationToken>()
                );
        }
        else
        {
            await _pending
                .DidNotReceive()
                .UpsertAsync(
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<DateTimeOffset>(),
                    Arg.Any<CancellationToken>()
                );
        }

        if (expectedDelete)
        {
            await _pending
                .Received(1)
                .RemoveAsync("org-1", "library-1", "page-1", Arg.Any<CancellationToken>());
            await _libraries
                .Received(1)
                .DeleteDocumentByExternalIdAsync(
                    "org-1",
                    "library-1",
                    "page-1",
                    Arg.Any<CancellationToken>()
                );
        }
        else
        {
            await _pending
                .DidNotReceive()
                .RemoveAsync(
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>()
                );
            await _libraries
                .DidNotReceive()
                .DeleteDocumentByExternalIdAsync(
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>()
                );
        }
    }

    [Test]
    public async Task HandleAsync_StaleCallback_NoOps()
    {
        _webhookState
            .ObserveSignedEventAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotionWebhookEventObservationResult.StaleCallback);

        await Handler().HandleAsync(Message("page.created"), CancellationToken.None);

        await _pending
            .DidNotReceive()
            .UpsertAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>()
            );
        await _libraries
            .DidNotReceive()
            .DeleteDocumentByExternalIdAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Test]
    public async Task HandleAsync_NonPageEntity_NoOpsAfterActivationObservation()
    {
        ArrangeCurrentWebhook();
        var message = new NotionPageChangeWebhookReceived
        {
            OrganizationId = "org-1",
            LibraryId = "library-1",
            CallbackTokenSerial = 5,
            SubscriptionId = "subscription-1",
            WorkspaceId = "workspace-1",
            NotionEventId = "event-1",
            EventType = "data_source.content_updated",
            EntityId = "data-source-1",
            EntityType = "data_source",
            AttemptNumber = 1,
            EventTimestamp = new DateTimeOffset(2026, 8, 8, 12, 0, 0, TimeSpan.Zero),
            TraceContext = new ZeeqTraceContext(null, null),
        };

        await Handler().HandleAsync(message, CancellationToken.None);

        await _pending
            .DidNotReceive()
            .UpsertAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>()
            );
    }

    private void ArrangeCurrentWebhook()
    {
        _webhookState
            .ObserveSignedEventAsync(
                "org-1",
                "library-1",
                5,
                "workspace-1",
                "subscription-1",
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotionWebhookEventObservationResult.Current);
    }

    private NotionPageChangeWebhookReceivedHandler Handler() =>
        new(
            Substitute.For<IDeadLetterWriter>(),
            _webhookState,
            _libraries,
            _pending,
            NullLogger<NotionPageChangeWebhookReceivedHandler>.Instance
        );

    private static NotionPageChangeWebhookReceived Message(string eventType) =>
        new()
        {
            OrganizationId = "org-1",
            LibraryId = "library-1",
            CallbackTokenSerial = 5,
            SubscriptionId = "subscription-1",
            WorkspaceId = "workspace-1",
            NotionEventId = "event-1",
            EventType = eventType,
            EntityId = "page-1",
            EntityType = "page",
            AttemptNumber = 1,
            EventTimestamp = new DateTimeOffset(2026, 8, 8, 12, 0, 0, TimeSpan.Zero),
            TraceContext = new ZeeqTraceContext(null, null),
        };
}
