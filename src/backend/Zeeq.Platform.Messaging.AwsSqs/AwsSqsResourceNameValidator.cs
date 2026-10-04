namespace Zeeq.Platform.Messaging.AwsSqs;

/// <summary>Rejects names that Brighter would silently normalize or truncate ambiguously.</summary>
public sealed class AwsSqsResourceNameValidator
{
    /// <summary>Maps a dotted logical route to an SQS standard queue, for example zeeq-orders_created_system.</summary>
    public string QueueName(string prefix, string routingKey)
    {
        if (prefix.Length is < 1 or > 16 || !prefix.All(IsQueueCharacter))
        {
            throw new ArgumentException(
                "QueuePrefix must contain 1–16 ASCII letters, digits, hyphens or underscores.",
                nameof(prefix)
            );
        }

        if (
            string.IsNullOrWhiteSpace(routingKey)
            || !routingKey.All(character => character == '.' || IsQueueCharacter(character))
        )
        {
            throw new ArgumentException($"Invalid SQS route '{routingKey}'.", nameof(routingKey));
        }

        var queueName = $"{prefix}-{routingKey.Replace('.', '_')}";
        if (queueName.Length > 80)
        {
            throw new ArgumentException(
                $"SQS queue '{queueName}' exceeds 80 characters.",
                nameof(routingKey)
            );
        }

        return queueName;
    }

    private static bool IsQueueCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '_';
}
