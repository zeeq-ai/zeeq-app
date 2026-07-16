using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Paramore.Brighter;
using Paramore.Brighter.JsonConverters;

namespace Zeeq.Platform.Messaging.Postgres;

/// <summary>
/// Writes failed messages to the app-owned PostgreSQL dead-letter sink.
/// </summary>
/// <remarks>
/// This writer runs from <see cref="ZeeqMessageHandler{TMessage}.FallbackAsync"/>
/// after the Brighter retry pipeline has given up on a message. It does not
/// publish to another Brighter queue. Instead, it writes an inspection record to
/// the app-owned dead-letter table so operators can see the original message,
/// route, attempt count, and failure details without another consumer picking
/// up the poison message automatically.
///
/// The active Brighter queue tables remain transport-owned. The dead-letter
/// table is Zeeq-owned replay storage: it preserves enough context for manual
/// diagnosis and future tooling, while leaving retry policy and message pump
/// behavior in Brighter.
/// </remarks>
public sealed class PostgresDeadLetterWriter(
    string connectionString,
    PostgresMessagingOptions options
) : IDeadLetterWriter
{
    private const string ChannelNameContextKey = "ChannelName";

    /// <summary>
    /// Persists a failed message and its Brighter pump context to the dead-letter table.
    /// </summary>
    /// <remarks>
    /// The method creates a JSONB payload with the typed request and the
    /// originating Brighter message, then stores the best available original
    /// queue name and the exception captured by the fallback policy. The insert
    /// is a direct PostgreSQL write rather than a Brighter publish, because the
    /// dead-letter path should terminate the failed processing flow.
    /// </remarks>
    /// <typeparam name="TMessage">Message type that failed processing.</typeparam>
    /// <param name="message">Failed message command after Brighter retries are exhausted.</param>
    /// <param name="context">Brighter request context from the message pump pipeline.</param>
    /// <param name="exception">Original exception captured by Brighter's fallback policy, if available.</param>
    /// <param name="cancellationToken">Cancellation token for the database write.</param>
    public async Task WriteAsync<TMessage>(
        TMessage message,
        IRequestContext? context,
        Exception? exception,
        CancellationToken cancellationToken = default
    )
        where TMessage : class, IRequest
    {
        var content = CreateContent(message, context);
        var originalQueue = ResolveOriginalQueue(context);
        var error = exception?.ToString() ?? "Brighter fallback executed without an exception.";

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();

        command.CommandText = $"""
            INSERT INTO {QuoteIdentifier(options.SchemaName)}.{QuoteIdentifier(
                options.DeadLetterTable
            )}
                ("original_queue", "content", "error", "attempt_count")
            VALUES
                (@original_queue, @content, @error, @attempt_count);
            """;

        command.Parameters.Add(
            new NpgsqlParameter<string>("original_queue", originalQueue)
            {
                NpgsqlDbType = NpgsqlDbType.Varchar,
            }
        );

        command.Parameters.Add(
            new NpgsqlParameter<string>("content", content) { NpgsqlDbType = NpgsqlDbType.Jsonb }
        );

        command.Parameters.Add(
            new NpgsqlParameter<string>("error", error) { NpgsqlDbType = NpgsqlDbType.Text }
        );

        command.Parameters.Add(
            new NpgsqlParameter<int>("attempt_count", ResolveAttemptCount(context))
            {
                NpgsqlDbType = NpgsqlDbType.Integer,
            }
        );

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string CreateContent<TMessage>(TMessage message, IRequestContext? context)
        where TMessage : class, IRequest
    {
        // Store both the mapped request and the raw Brighter message. The request is
        // convenient for feature debugging; the raw message preserves headers and
        // transport metadata needed for future replay tooling.
        var content = new DeadLetterContent(
            MessageType: typeof(TMessage).FullName ?? typeof(TMessage).Name,
            MessageId: message.Id.Value,
            CorrelationId: message.CorrelationId?.Value,
            Request: message,
            OriginatingMessage: context?.OriginatingMessage
        );

        return JsonSerializer.Serialize(content, JsonSerialisationOptions.Options);
    }

    private static string ResolveOriginalQueue(IRequestContext? context)
    {
        // Brighter places the subscription channel in the context bag while the
        // message pump is running. Prefer it because it names the channel that
        // actually failed. Fall back to message headers or producer destination
        // for tests and non-standard fallback paths.
        if (context?.Bag.TryGetValue(ChannelNameContextKey, out var channelName) == true)
        {
            return channelName.ToString() ?? string.Empty;
        }

        return context?.OriginatingMessage?.Header.Topic.Value
            ?? context?.Destination?.RoutingKey.Value
            ?? string.Empty;
    }

    private static int ResolveAttemptCount(IRequestContext? context)
    {
        var originatingMessage = context?.OriginatingMessage;
        if (originatingMessage is null)
        {
            return 1;
        }

        // Brighter increments HandledCount for completed handling attempts. The
        // fallback write represents the failed attempt currently being handled,
        // so add one and keep the stored value human-readable from one.
        return Math.Max(1, originatingMessage.Header.HandledCount + 1);
    }

    private static string QuoteIdentifier(string identifier) =>
        $""""{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}"""";

    /// <summary>
    /// JSON payload stored in the dead-letter table's <c>content</c> column.
    /// </summary>
    /// <param name="MessageType">Fully qualified CLR message type.</param>
    /// <param name="MessageId">Brighter request identifier.</param>
    /// <param name="CorrelationId">Optional Brighter request correlation identifier.</param>
    /// <param name="Request">Mapped request object passed to the feature handler.</param>
    /// <param name="OriginatingMessage">Raw Brighter transport message, including headers.</param>
    private sealed record DeadLetterContent(
        string MessageType,
        string MessageId,
        string? CorrelationId,
        object Request,
        Message? OriginatingMessage
    );
}
