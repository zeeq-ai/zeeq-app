using System.Reflection;

namespace Zeeq.Platform.Messaging;

/// <summary>
/// Scans assemblies for Zeeq publisher and consumer metadata.
/// </summary>
public sealed class MessagingCatalogScanner
{
    /// <summary>
    /// Scans the supplied assemblies and returns discovered messaging metadata.
    /// </summary>
    /// <param name="assemblies">Assemblies that may contain message types or handlers.</param>
    /// <returns>Discovered messaging catalog.</returns>
    public MessagingCatalog Scan(params Assembly[] assemblies)
    {
        var types = assemblies
            .Distinct()
            .SelectMany(GetLoadableTypes)
            .Where(type => type is { IsAbstract: false, IsGenericTypeDefinition: false })
            .ToArray();

        var publishers = types
            .Select(GetPublisher)
            .Where(publisher => publisher is not null)
            .Cast<MessagingPublisher>()
            .ToArray();

        var consumers = types
            .Select(GetConsumer)
            .Where(consumer => consumer is not null)
            .Cast<MessagingConsumer>()
            .ToArray();

        return new MessagingCatalog(publishers, consumers);
    }

    private static MessagingPublisher? GetPublisher(Type type)
    {
        var publisher = type.GetCustomAttribute<ConfigurePublisherAttributeBase>(inherit: false);
        if (publisher is null)
        {
            return null;
        }

        return new MessagingPublisher(
            type,
            publisher.Topic,
            publisher.PriorityType,
            publisher.VisibleTimeoutSeconds,
            publisher.BufferSize,
            typeof(ITenantMessage).IsAssignableFrom(type),
            typeof(ISystemMessage).IsAssignableFrom(type)
        );
    }

    private static MessagingConsumer? GetConsumer(Type type)
    {
        var consumer = type.GetCustomAttribute<ConfigureConsumerAttribute>(inherit: false);
        if (consumer is null)
        {
            return null;
        }

        return new MessagingConsumer(
            type,
            consumer.MessageType,
            consumer.ChannelName,
            consumer.NoOfPerformers,
            consumer.BufferSize,
            consumer.VisibleTimeoutSeconds,
            consumer.PollIntervalMilliseconds
        );
    }

    private static IReadOnlyList<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(type => type is not null).Cast<Type>().ToArray();
        }
    }
}
