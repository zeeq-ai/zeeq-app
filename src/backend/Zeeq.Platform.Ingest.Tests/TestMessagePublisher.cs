using Zeeq.Platform.Messaging;
using Paramore.Brighter;

namespace Zeeq.Platform.Ingest.Tests;

/// <summary>In-memory <see cref="IZeeqMessagePublisher"/> that records published messages.</summary>
internal sealed class TestMessagePublisher : IZeeqMessagePublisher
{
    public List<IRequest> Published { get; } = [];

    public Task PublishAsync<TMessage>(
        TMessage message,
        CancellationToken cancellationToken = default
    )
        where TMessage : class, IRequest
    {
        Published.Add(message);
        return Task.CompletedTask;
    }

    public Task PublishAfterAsync<TMessage>(
        TMessage message,
        TimeSpan delay,
        CancellationToken cancellationToken = default
    )
        where TMessage : class, IRequest
    {
        Published.Add(message);
        return Task.CompletedTask;
    }
}
