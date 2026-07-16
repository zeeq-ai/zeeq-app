using Paramore.Brighter;

namespace Zeeq.Platform.Messaging;

/// <summary>
/// Validates discovered messaging metadata before transport registration.
/// </summary>
public sealed class MessagingConventionValidator
{
    /// <summary>
    /// Returns all convention violations found in the catalog.
    /// </summary>
    /// <param name="catalog">Catalog to validate.</param>
    /// <param name="options">Messaging options used for priority validation.</param>
    /// <returns>Validation error messages. Empty means the catalog is valid.</returns>
    public IReadOnlyList<string> Validate(
        MessagingCatalog catalog,
        ZeeqMessagingOptions? options = null
    )
    {
        var errors = new List<string>();
        ValidatePublishers(catalog, options ?? new ZeeqMessagingOptions(), errors);
        ValidateConsumers(catalog, errors);
        return errors;
    }

    /// <summary>
    /// Throws when the catalog violates Zeeq messaging conventions.
    /// </summary>
    /// <param name="catalog">Catalog to validate.</param>
    /// <param name="options">Messaging options used for priority validation.</param>
    public void ValidateAndThrow(MessagingCatalog catalog, ZeeqMessagingOptions? options = null)
    {
        var errors = Validate(catalog, options);
        if (errors.Count > 0)
        {
            throw new MessagingCatalogValidationException(errors);
        }
    }

    private static void ValidatePublishers(
        MessagingCatalog catalog,
        ZeeqMessagingOptions options,
        List<string> errors
    )
    {
        foreach (var publisher in catalog.Publishers)
        {
            if (string.IsNullOrWhiteSpace(publisher.Topic))
            {
                errors.Add($"{publisher.MessageType.FullName} declares an empty publisher topic.");
            }

            if (publisher is { IsTenantMessage: false, IsSystemMessage: false })
            {
                errors.Add(
                    $"{publisher.MessageType.FullName} must implement {nameof(ITenantMessage)} or {nameof(ISystemMessage)}."
                );
            }

            if (!typeof(IRequest).IsAssignableFrom(publisher.MessageType))
            {
                errors.Add(
                    $"{publisher.MessageType.FullName} must implement {nameof(IRequest)} so Brighter can publish and map it."
                );
            }

            if (publisher is { IsTenantMessage: true, IsSystemMessage: true })
            {
                errors.Add(
                    $"{publisher.MessageType.FullName} must not implement both {nameof(ITenantMessage)} and {nameof(ISystemMessage)}."
                );
            }

            if (publisher is { IsImmediateMessage: true, IsTenantMessage: false })
            {
                errors.Add(
                    $"{publisher.MessageType.FullName} uses {nameof(ImmediateMessage)} and must implement {nameof(ITenantMessage)} so tenant identity travels with immediate work."
                );
            }

            if (!typeof(IMessagePriority).IsAssignableFrom(publisher.PriorityType))
            {
                errors.Add(
                    $"{publisher.MessageType.FullName} declares unsupported priority marker {publisher.PriorityType.FullName}."
                );
            }

            if (!options.PriorityDefaults.ContainsKey(publisher.PriorityType))
            {
                errors.Add(
                    $"{publisher.MessageType.FullName} uses priority {publisher.PriorityType.Name}, but no default options are configured for that priority."
                );
            }
        }

        foreach (
            var duplicateTopic in catalog
                .Publishers.GroupBy(publisher => publisher.Topic)
                .Where(g => g.Count() > 1)
        )
        {
            var messageTypes = duplicateTopic
                .Select(publisher => publisher.MessageType.FullName)
                .Order()
                .ToArray();

            errors.Add(
                $"Publisher topic '{duplicateTopic.Key}' is declared by multiple message types: {string.Join(", ", messageTypes)}."
            );
        }
    }

    private static void ValidateConsumers(MessagingCatalog catalog, List<string> errors)
    {
        foreach (var consumer in catalog.Consumers)
        {
            if (string.IsNullOrWhiteSpace(consumer.ChannelName))
            {
                errors.Add($"{consumer.HandlerType.FullName} declares an empty consumer channel.");
            }

            if (catalog.FindPublisher(consumer.MessageType) is null)
            {
                errors.Add(
                    $"{consumer.HandlerType.FullName} consumes {consumer.MessageType.FullName}, but that message has no publisher declaration."
                );
            }

            if (!IsRequestHandlerFor(consumer.HandlerType, consumer.MessageType))
            {
                errors.Add(
                    $"{consumer.HandlerType.FullName} must inherit RequestHandlerAsync<T> for {consumer.MessageType.FullName}."
                );
            }
        }
    }

    private static bool IsRequestHandlerFor(Type handlerType, Type messageType)
    {
        for (var current = handlerType; current is not null; current = current.BaseType)
        {
            if (
                current.IsGenericType
                && current.GetGenericTypeDefinition() == typeof(RequestHandlerAsync<>)
                && current.GenericTypeArguments[0] == messageType
            )
            {
                return true;
            }
        }

        return false;
    }
}
