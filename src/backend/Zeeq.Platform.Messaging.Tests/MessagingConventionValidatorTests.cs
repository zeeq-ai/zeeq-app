using Paramore.Brighter;

namespace Zeeq.Platform.Messaging.Tests;

public sealed class MessagingConventionValidatorTests
{
    private readonly MessagingConventionValidator _validator = new();

    [Test]
    public async Task Validate_WithValidCatalog_ReturnsNoErrors()
    {
        var catalog = new MessagingCatalog(
            [
                new MessagingPublisher(
                    typeof(ValidatorTenantMessage),
                    "validator.tenant",
                    typeof(DefaultMessage),
                    VisibleTimeoutSeconds: 30,
                    BufferSize: 10,
                    IsTenantMessage: true,
                    IsSystemMessage: false
                ),
            ],
            [
                new MessagingConsumer(
                    typeof(ValidatorTenantHandler),
                    typeof(ValidatorTenantMessage),
                    "validator.tenant.worker",
                    NoOfPerformers: 1,
                    BufferSize: 10,
                    VisibleTimeoutSeconds: 30,
                    PollIntervalMilliseconds: 1000
                ),
            ]
        );

        var errors = _validator.Validate(catalog);

        await Assert.That(errors).IsEmpty();
    }

    [Test]
    public async Task Validate_WithDuplicateTopics_ReturnsTopicError()
    {
        var catalog = new MessagingCatalog(
            [
                Publisher(typeof(ValidatorTenantMessage), topic: "duplicate.topic"),
                Publisher(typeof(SecondValidatorTenantMessage), topic: "duplicate.topic"),
            ],
            []
        );

        var errors = _validator.Validate(catalog);

        await Assert.That(errors.Single()).Contains("duplicate.topic");
    }

    [Test]
    public async Task Validate_WithConsumerWithoutPublisher_ReturnsMissingPublisherError()
    {
        var catalog = new MessagingCatalog(
            [],
            [
                new MessagingConsumer(
                    typeof(ValidatorTenantHandler),
                    typeof(ValidatorTenantMessage),
                    "validator.tenant.worker",
                    NoOfPerformers: 1,
                    BufferSize: 10,
                    VisibleTimeoutSeconds: 30,
                    PollIntervalMilliseconds: 1000
                ),
            ]
        );

        var errors = _validator.Validate(catalog);

        await Assert.That(errors.Single()).Contains("no publisher declaration");
    }

    [Test]
    public async Task Validate_WithHandlerThatDoesNotInheritRequestHandler_ReturnsHandlerError()
    {
        var catalog = new MessagingCatalog(
            [Publisher(typeof(ValidatorTenantMessage))],
            [
                new MessagingConsumer(
                    typeof(NotAHandler),
                    typeof(ValidatorTenantMessage),
                    "validator.tenant.worker",
                    NoOfPerformers: 1,
                    BufferSize: 10,
                    VisibleTimeoutSeconds: 30,
                    PollIntervalMilliseconds: 1000
                ),
            ]
        );

        var errors = _validator.Validate(catalog);

        await Assert.That(errors.Single()).Contains("must inherit RequestHandlerAsync<T>");
    }

    [Test]
    public async Task Validate_WithOutOfRangeNumericValues_ReturnsNoErrors()
    {
        var catalog = new MessagingCatalog(
            [Publisher(typeof(ValidatorTenantMessage))],
            [
                new MessagingConsumer(
                    typeof(ValidatorTenantHandler),
                    typeof(ValidatorTenantMessage),
                    "validator.tenant.worker",
                    NoOfPerformers: -1,
                    BufferSize: 100,
                    VisibleTimeoutSeconds: -30,
                    PollIntervalMilliseconds: -1000
                ),
            ]
        );

        var errors = _validator.Validate(catalog);

        await Assert.That(errors).IsEmpty();
    }

    [Test]
    public async Task Validate_WithMessageWithoutRoutingMarker_ReturnsMarkerError()
    {
        var catalog = new MessagingCatalog(
            [
                new MessagingPublisher(
                    typeof(ValidatorUnscopedMessage),
                    "validator.unscoped",
                    typeof(DefaultMessage),
                    VisibleTimeoutSeconds: 30,
                    BufferSize: 10,
                    IsTenantMessage: false,
                    IsSystemMessage: false
                ),
            ],
            []
        );

        var errors = _validator.Validate(catalog);

        await Assert
            .That(errors.Single())
            .Contains("must implement ITenantMessage or ISystemMessage");
    }

    [Test]
    public async Task Validate_WithPublisherThatIsNotBrighterRequest_ReturnsRequestError()
    {
        var catalog = new MessagingCatalog(
            [
                new MessagingPublisher(
                    typeof(NotABrighterRequest),
                    "validator.invalid",
                    typeof(DefaultMessage),
                    VisibleTimeoutSeconds: 30,
                    BufferSize: 10,
                    IsTenantMessage: true,
                    IsSystemMessage: false
                ),
            ],
            []
        );

        var errors = _validator.Validate(catalog);

        await Assert.That(errors.Single()).Contains("must implement IRequest");
    }

    [Test]
    public async Task Validate_WithImmediateMessageWithoutTenantMarker_ReturnsTenantIdentityError()
    {
        var catalog = new MessagingCatalog(
            [
                new MessagingPublisher(
                    typeof(ImmediateSystemMessage),
                    "validator.immediate",
                    typeof(ImmediateMessage),
                    VisibleTimeoutSeconds: 30,
                    BufferSize: 10,
                    IsTenantMessage: false,
                    IsSystemMessage: true
                ),
            ],
            []
        );

        var errors = _validator.Validate(catalog);

        await Assert
            .That(errors)
            .Contains(error => error.Contains("must implement ITenantMessage"));
    }

    private static MessagingPublisher Publisher(
        Type messageType,
        string topic = "validator.tenant"
    ) =>
        new(
            messageType,
            topic,
            typeof(DefaultMessage),
            VisibleTimeoutSeconds: 30,
            BufferSize: 10,
            IsTenantMessage: true,
            IsSystemMessage: false
        );

    private sealed class ValidatorTenantMessage(string organizationId, string? teamId)
        : Event(Id.Random()),
            ITenantMessage
    {
        public string OrganizationId { get; } = organizationId;

        public string? TeamId { get; } = teamId;
    }

    private sealed class SecondValidatorTenantMessage(string organizationId, string? teamId)
        : Event(Id.Random()),
            ITenantMessage
    {
        public string OrganizationId { get; } = organizationId;

        public string? TeamId { get; } = teamId;
    }

    private sealed class ValidatorUnscopedMessage : Event
    {
        public ValidatorUnscopedMessage()
            : base(Id.Random()) { }
    }

    private sealed class ValidatorTenantHandler(IDeadLetterWriter deadLetterWriter)
        : ZeeqMessageHandler<ValidatorTenantMessage>(deadLetterWriter)
    {
        protected override Task<ValidatorTenantMessage> HandleMessageAsync(
            ValidatorTenantMessage message,
            CancellationToken cancellationToken
        ) => Task.FromResult(message);
    }

    private sealed class NotAHandler;

    private sealed class NotABrighterRequest : ITenantMessage
    {
        public string OrganizationId { get; } = "org_123";

        public string? TeamId { get; } = null;
    }

    private sealed class ImmediateSystemMessage : Event, ISystemMessage
    {
        public ImmediateSystemMessage()
            : base(Id.Random()) { }
    }
}
