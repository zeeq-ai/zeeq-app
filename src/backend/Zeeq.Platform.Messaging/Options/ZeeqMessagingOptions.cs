namespace Zeeq.Platform.Messaging;

/// <summary>
/// Runtime options for transport-neutral Zeeq messaging behavior.
/// </summary>
/// <remarks>
/// These options control generated Brighter transport metadata without putting
/// Postgres table names or Brighter registration details in feature assemblies.
/// Feature code declares intent with <see cref="ConfigurePublisherAttribute"/>
/// and <see cref="ConfigureConsumerAttribute{TMessage}"/>. Runtime composition
/// binds this object from the <c>ZeeqMessaging</c> configuration section and
/// transport adapters call <see cref="MessagingDefaultsResolver"/> to turn the
/// discovered catalog plus these options into concrete subscriptions and
/// publications.
///
/// Defaults are layered from broadest to narrowest:
/// <list type="number">
/// <item><description>Built-in <see cref="MessagingDefaultsOptions.Standard"/> values.</description></item>
/// <item><description><see cref="Defaults"/> for application-wide behavior.</description></item>
/// <item><description><see cref="PriorityDefaults"/> for a class of work such as priority or low-priority jobs.</description></item>
/// <item><description><see cref="TopicOverrides"/> for one logical topic.</description></item>
/// <item><description>Explicit values on <see cref="ConfigurePublisherAttribute"/> and <see cref="ConfigureConsumerAttribute{TMessage}"/>.</description></item>
/// </list>
/// Attribute values use <c>0</c> to inherit the configured defaults. Non-zero
/// numeric values are clamped by the resolver before transport registration so
/// a bad configuration does not create unusable Brighter subscriptions.
/// </remarks>
/// <example>
/// Use appsettings for environment-level tuning that should not require a code
/// change. This example slows all polling slightly, then gives one noisy topic
/// a smaller fetch size and longer visibility timeout.
/// <code>
/// {
///   "ZeeqMessaging": {
///     "Defaults": {
///       "BufferSize": 5,
///       "NoOfPerformers": 1,
///       "VisibleTimeoutSeconds": 60,
///       "PollIntervalMilliseconds": 1500
///     },
///     "TopicOverrides": {
///       "reports.refresh": {
///         "BufferSize": 2,
///         "VisibleTimeoutSeconds": 300
///       }
///     }
///   }
/// }
/// </code>
/// </example>
public sealed class ZeeqMessagingOptions
{
    /// <summary>
    /// Configuration section name for binding.
    /// </summary>
    /// <remarks>
    /// Runtime setup reads this section once when registering producers and
    /// consumers. The same section is used by web mode, inline consumer mode,
    /// and standalone worker mode so local and production behavior can stay in
    /// sync.
    /// </remarks>
    public const string SectionName = "ZeeqMessaging";

    /// <summary>
    /// Global defaults used when no topic or priority override exists.
    /// </summary>
    /// <remarks>
    /// Use this for the normal operating profile of the application: baseline
    /// batch size, subscription concurrency, visibility timeout, and empty-poll
    /// delay. These values apply to every publisher/consumer unless a priority,
    /// topic, or attribute override replaces an individual setting.
    ///
    /// Prefer <see cref="Defaults"/> when changing behavior for an environment,
    /// such as reducing polling pressure in local development or increasing the
    /// visibility timeout in a slower staging database. Prefer narrower
    /// overrides when only one priority class or topic needs different behavior.
    /// </remarks>
    /// <example>
    /// A local developer profile can reduce database polling by increasing the
    /// empty-channel delay for all generated subscriptions.
    /// <code>
    /// {
    ///   "ZeeqMessaging": {
    ///     "Defaults": {
    ///       "PollIntervalMilliseconds": 5000
    ///     }
    ///   }
    /// }
    /// </code>
    /// </example>
    public MessagingDefaultsOptions Defaults { get; init; } = MessagingDefaultsOptions.Standard;

    /// <summary>
    /// Priority-specific defaults keyed by approved priority marker type.
    /// </summary>
    /// <remarks>
    /// Message types select one of these entries by using
    /// <see cref="ConfigurePublisherAttribute{TPriority}"/>. The resolver starts
    /// with <see cref="Defaults"/>, then applies the matching priority defaults
    /// for the publisher's priority marker. This is how Zeeq makes an entire
    /// class of work faster or slower without repeating settings on every
    /// message type.
    ///
    /// Use priority defaults when the behavior should follow the work class:
    /// <see cref="PriorityMessage"/> can poll more often or use more performers,
    /// while <see cref="LowPriorityMessage"/> can poll less often to reduce
    /// database pressure. Do not use priority defaults for one-off exceptions;
    /// use <see cref="TopicOverrides"/> or explicit attribute values for those.
    ///
    /// These keys are marker <see cref="Type"/> instances, so changing this map
    /// is normally a code/composition decision rather than plain appsettings
    /// tuning. If the map is replaced, keep entries for every approved priority
    /// marker used by feature messages. Startup validation fails when a
    /// publisher declares a priority marker that has no configured defaults.
    /// </remarks>
    /// <example>
    /// A message opts into priority behavior by selecting the marker on its
    /// publisher attribute. The topic remains the same logical topic; the
    /// marker only influences default transport settings.
    /// <code>
    /// [ConfigurePublisher&lt;PriorityMessage&gt;("github.webhook.received")]
    /// public sealed record GitHubWebhookReceived(
    ///     string OrganizationId,
    ///     string? TeamId,
    ///     string DeliveryId
    /// ) : Event(Id.Random()), ITenantMessage;
    /// </code>
    /// </example>
    /// <example>
    /// Runtime composition can replace the priority map when a deployment wants
    /// a different profile for an entire work class.
    /// <code>
    /// var options = new ZeeqMessagingOptions
    /// {
    ///     PriorityDefaults = new Dictionary&lt;Type, MessagingDefaultsOptions&gt;
    ///     {
    ///         [typeof(PriorityMessage)] = new()
    ///         {
    ///             NoOfPerformers = 4,
    ///             PollIntervalMilliseconds = 250,
    ///         },
    ///         [typeof(DefaultMessage)] = new(),
    ///         [typeof(LowPriorityMessage)] = new()
    ///         {
    ///             PollIntervalMilliseconds = 5000,
    ///         },
    ///     },
    /// };
    /// </code>
    /// </example>
    public IReadOnlyDictionary<Type, MessagingDefaultsOptions> PriorityDefaults { get; init; } =
        new Dictionary<Type, MessagingDefaultsOptions>
        {
            [typeof(PriorityMessage)] = new()
            {
                NoOfPerformers = 2,
                PollIntervalMilliseconds = 500,
            },
            [typeof(DefaultMessage)] = new(),
            [typeof(LowPriorityMessage)] = new() { PollIntervalMilliseconds = 2000 },
            [typeof(ImmediateMessage)] = new()
            {
                // Brighter's async channel caps buffer size at 10. Immediate
                // throughput comes from more performers and faster polling.
                BufferSize = 10,
                NoOfPerformers = 16,
                VisibleTimeoutSeconds = 60,
                PollIntervalMilliseconds = 50,
            },
        };

    /// <summary>
    /// Topic-specific overrides keyed by logical publisher topic.
    /// </summary>
    /// <remarks>
    /// Topic overrides are applied after priority defaults and before explicit
    /// attribute overrides. Use them when one logical topic needs different
    /// behavior from the rest of its priority class. For example, most
    /// low-priority messages may poll slowly, but a specific low-priority topic
    /// that performs short idempotent work can fetch a larger batch.
    ///
    /// The dictionary key is the logical topic from
    /// <see cref="ConfigurePublisherAttribute"/>. It is not a concrete Postgres
    /// routing key such as <c>reports.refresh.default.03</c>; tenant tier and
    /// bucket expansion happens later in the transport adapter.
    /// </remarks>
    /// <example>
    /// Use a topic override for a specific slow handler without changing every
    /// default-priority message.
    /// <code>
    /// {
    ///   "ZeeqMessaging": {
    ///     "TopicOverrides": {
    ///       "reports.refresh": {
    ///         "BufferSize": 1,
    ///         "VisibleTimeoutSeconds": 600
    ///       }
    ///     }
    ///   }
    /// }
    /// </code>
    /// </example>
    public Dictionary<string, MessagingDefaultsOptions> TopicOverrides { get; init; } = [];

    /// <summary>
    /// Tenant bucket routing defaults. Transport adapters may combine these with
    /// their own table mapping options.
    /// </summary>
    /// <remarks>
    /// Tenant bucket settings control fairness and distribution for
    /// <see cref="ITenantMessage"/> publishers. They decide how many stable
    /// bucket routes exist per organization tier before a transport adapter maps
    /// those routes to queue tables and subscriptions. Increasing bucket counts
    /// spreads tenant traffic across more queues; decreasing them requires care
    /// because old bucket routes may still contain queued messages.
    ///
    /// Use this when changing capacity allocation between priority, default, and
    /// low tenant tiers. Use <see cref="Defaults"/>,
    /// <see cref="PriorityDefaults"/>, or <see cref="TopicOverrides"/> when
    /// changing handler concurrency, fetch size, timeouts, or polling delay.
    /// </remarks>
    /// <example>
    /// A deployment can allocate more queue buckets to default-tier tenants
    /// while keeping low-priority tenants on fewer buckets.
    /// <code>
    /// {
    ///   "ZeeqMessaging": {
    ///     "TenantBuckets": {
    ///       "PriorityBucketCount": 4,
    ///       "DefaultBucketCount": 8,
    ///       "LowBucketCount": 2
    ///     }
    ///   }
    /// }
    /// </code>
    /// </example>
    public TenantBucketRoutingOptions TenantBuckets { get; init; } = new();
}
