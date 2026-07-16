using Zeeq.Core.Models;

namespace Zeeq.Platform.Telemetry.Adapters;

/// <summary>
/// Base class for agent telemetry adapters. Provides shared utilities and default
/// implementations for the signal type the adapter does not handle — log adapters
/// return <c>false</c> for spans, and span adapters return <c>false</c> for logs.
/// </summary>
/// <remarks>
/// Adapters that handle spans override <see cref="CanHandle(TelemetrySpanRecordContext)"/>
/// and <see cref="Adapt(TelemetrySpanRecordContext)"/>. Adapters that handle logs
/// override the log-context counterparts. The base defaults let each adapter focus
/// on its one signal type.
/// </remarks>
public abstract class AgentTelemetryAdapterBase : IAgentTelemetryAdapter
{
    /// <inheritdoc />
    public abstract string HarnessName { get; }

    /// <inheritdoc />
    public abstract bool CanHandle(TelemetryLogRecordContext record);

    /// <inheritdoc />
    public abstract AgentTelemetryAdapterResult Adapt(TelemetryLogRecordContext record);

    /// <inheritdoc />
    public virtual bool CanHandle(TelemetrySpanRecordContext record) => false;

    /// <inheritdoc />
    public virtual AgentTelemetryAdapterResult Adapt(TelemetrySpanRecordContext record) =>
        throw new NotSupportedException(
            $"Span adaptation is not supported by {GetType().Name}. Span-capable adapters must override this method."
        );

    /// <summary>
    /// Normalizes absent or whitespace-only harness attributes to <see langword="null" />.
    /// </summary>
    protected static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
