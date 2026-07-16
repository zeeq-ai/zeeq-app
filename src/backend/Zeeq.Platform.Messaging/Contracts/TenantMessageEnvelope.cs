namespace Zeeq.Platform.Messaging;

/// <summary>
/// Common tenant metadata that can be embedded in feature-owned message records.
/// </summary>
/// <remarks>
/// Use this record when a message needs to carry tenant scope plus correlation
/// metadata as a single value. The message type still implements
/// <see cref="ITenantMessage"/> directly because the messaging router reads
/// <see cref="ITenantMessage.OrganizationId"/> and <see cref="ITenantMessage.TeamId"/>
/// from the message contract, not from nested payload values.
/// </remarks>
/// <example>
/// A feature message can expose the envelope for handler context while
/// forwarding the routing fields through <see cref="ITenantMessage"/>.
/// <code>
/// [ConfigurePublisher&lt;DefaultMessage&gt;("reports.refresh")]
/// public sealed record RefreshReportMessage(
///     TenantMessageEnvelope Tenant,
///     string ReportId
/// ) : Event(Id.Random()), ITenantMessage
/// {
///     public string OrganizationId =&gt; Tenant.OrganizationId;
///
///     public string? TeamId =&gt; Tenant.TeamId;
/// }
/// </code>
/// </example>
/// <param name="OrganizationId">Organization that owns the queued work.</param>
/// <param name="TeamId">Optional team that scopes the queued work.</param>
/// <param name="CorrelationId">Optional external correlation identifier.</param>
/// <param name="CausationId">Optional identifier for the operation that caused this message.</param>
public sealed record TenantMessageEnvelope(
    string OrganizationId,
    string? TeamId = null,
    string? CorrelationId = null,
    string? CausationId = null
);
