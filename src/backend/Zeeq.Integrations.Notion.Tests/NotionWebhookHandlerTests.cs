using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Zeeq.Core.Documents;
using Zeeq.Core.Models;
using Zeeq.Core.Security;
using Zeeq.Platform.Messaging;

namespace Zeeq.Integrations.Notion.Tests;

public sealed class NotionWebhookHandlerTests
{
    private const string OrganizationId = "org-1";
    private const string LibraryId = "library-1";
    private const int CallbackSerial = 4;
    private readonly NotionCallbackTokenProtector _callbackTokens = new(
        new EphemeralDataProtectionProvider()
    );
    private readonly ILibraryDocumentStore _libraries = Substitute.For<ILibraryDocumentStore>();
    private readonly IEncryptedValueStore _encryptedValues = Substitute.For<IEncryptedValueStore>();
    private readonly INotionWebhookStore _webhookState = Substitute.For<INotionWebhookStore>();
    private readonly IZeeqMessagePublisher _publisher = Substitute.For<IZeeqMessagePublisher>();
    private readonly EncryptedValueEncryptionService _encryption;

    public NotionWebhookHandlerTests()
    {
        _encryption = new EncryptedValueEncryptionService(
            new SecuritySettings
            {
                EncryptionProvider = "test",
                DataProtectionKeyRingPath = string.Empty,
                GoogleKmsKeyName = string.Empty,
            },
            [new PassthroughEncryptionProvider()]
        );
    }

    [Test]
    public async Task HandleAsync_Challenge_EncryptsAndDelegatesAtomicCapture()
    {
        ArrangeLibrary();
        _webhookState
            .TryCaptureVerificationTokenAsync(
                OrganizationId,
                LibraryId,
                CallbackSerial,
                Arg.Any<EncryptedValue>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotionWebhookVerificationCaptureResult.Stored);
        EncryptedValue? capturedToken = null;
        _webhookState
            .When(store =>
                store.TryCaptureVerificationTokenAsync(
                    OrganizationId,
                    LibraryId,
                    CallbackSerial,
                    Arg.Any<EncryptedValue>(),
                    Arg.Any<DateTimeOffset>(),
                    Arg.Any<CancellationToken>()
                )
            )
            .Do(call => capturedToken = call.ArgAt<EncryptedValue>(3));

        var result = await Handler()
            .HandleAsync(
                CallbackToken(),
                Encoding.UTF8.GetBytes("""{"verification_token":"verify-me"}"""),
                signatureHeader: null,
                CancellationToken.None
            );

        await Assert.That(result).IsEqualTo(NotionWebhookIngressResult.Accepted);
        await Assert.That(capturedToken).IsNotNull();
        await Assert.That(capturedToken!.Kind).IsEqualTo(EncryptedValueKind.SecretString);
        await Assert.That(Encoding.UTF8.GetString(capturedToken.Ciphertext)).IsEqualTo("verify-me");
        await _publisher
            .DidNotReceive()
            .PublishAsync(Arg.Any<NotionPageChangeWebhookReceived>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HandleAsync_ChallengeAlreadyCaptured_ReturnsConflict()
    {
        ArrangeLibrary();
        _webhookState
            .TryCaptureVerificationTokenAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<EncryptedValue>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(NotionWebhookVerificationCaptureResult.AlreadyStored);

        var result = await Handler()
            .HandleAsync(
                CallbackToken(),
                Encoding.UTF8.GetBytes("""{"verification_token":"verify-me"}"""),
                signatureHeader: null,
                CancellationToken.None
            );

        await Assert.That(result).IsEqualTo(NotionWebhookIngressResult.Conflict);
    }

    [Test]
    public async Task HandleAsync_SignedEventWithInvalidSignature_DoesNotPublish()
    {
        ArrangeLibrary(verificationTokenValueId: "enc-verification");
        ArrangeVerificationToken("verify-me");
        var rawBody = SignedEventBody();

        var result = await Handler()
            .HandleAsync(
                CallbackToken(),
                rawBody,
                "sha256=0000000000000000000000000000000000000000000000000000000000000000",
                CancellationToken.None
            );

        await Assert.That(result).IsEqualTo(NotionWebhookIngressResult.Unauthorized);
        await _publisher
            .DidNotReceive()
            .PublishAsync(Arg.Any<NotionPageChangeWebhookReceived>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HandleAsync_ValidSignedEvent_PublishesTenantAndActivationMetadata()
    {
        const string verificationToken = "verify-me";
        ArrangeLibrary(verificationTokenValueId: "enc-verification");
        ArrangeVerificationToken(verificationToken);
        var rawBody = SignedEventBody();
        var signature =
            "sha256="
            + Convert.ToHexStringLower(
                HMACSHA256.HashData(Encoding.UTF8.GetBytes(verificationToken), rawBody)
            );

        var result = await Handler()
            .HandleAsync(CallbackToken(), rawBody, signature, CancellationToken.None);

        await Assert.That(result).IsEqualTo(NotionWebhookIngressResult.Accepted);
        await _publisher
            .Received(1)
            .PublishAsync(
                Arg.Is<NotionPageChangeWebhookReceived>(message =>
                    message.OrganizationId == OrganizationId
                    && message.LibraryId == LibraryId
                    && message.CallbackTokenSerial == CallbackSerial
                    && message.WorkspaceId == "workspace-1"
                    && message.SubscriptionId == "subscription-1"
                    && message.EventType == "page.created"
                    && message.EntityId == "page-1"
                ),
                Arg.Any<CancellationToken>()
            );
    }

    [Test]
    public async Task HandleAsync_SignedEventWithWhitespaceRoutingField_ReturnsBadRequest()
    {
        const string verificationToken = "verify-me";
        ArrangeLibrary(verificationTokenValueId: "enc-verification");
        ArrangeVerificationToken(verificationToken);
        var rawBody = Encoding.UTF8.GetBytes(
            """
            {"id":"event-1","type":"page.created","subscription_id":" ","workspace_id":"workspace-1","attempt_number":1,"entity":{"id":"page-1","type":"page"}}
            """
        );
        var signature =
            "sha256="
            + Convert.ToHexStringLower(
                HMACSHA256.HashData(Encoding.UTF8.GetBytes(verificationToken), rawBody)
            );

        var result = await Handler()
            .HandleAsync(CallbackToken(), rawBody, signature, CancellationToken.None);

        await Assert.That(result).IsEqualTo(NotionWebhookIngressResult.BadRequest);
        await _publisher
            .DidNotReceive()
            .PublishAsync(Arg.Any<NotionPageChangeWebhookReceived>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HandleAsync_StaleCallbackSerial_ReturnsNotFoundBeforeSecretLookup()
    {
        ArrangeLibrary(callbackSerial: CallbackSerial + 1, verificationTokenValueId: "enc-token");

        var result = await Handler()
            .HandleAsync(
                CallbackToken(),
                SignedEventBody(),
                signatureHeader: null,
                CancellationToken.None
            );

        await Assert.That(result).IsEqualTo(NotionWebhookIngressResult.NotFound);
        await _encryptedValues
            .DidNotReceive()
            .FindActiveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private NotionWebhookHandler Handler() =>
        new(
            _callbackTokens,
            new NotionWebhookSignatureVerifier(),
            _libraries,
            _encryptedValues,
            _encryption,
            _webhookState,
            _publisher,
            NullLogger<NotionWebhookHandler>.Instance
        );

    private string CallbackToken() =>
        _callbackTokens.Protect(
            new NotionCallbackPayload(OrganizationId, LibraryId, CallbackSerial)
        );

    private void ArrangeLibrary(
        int callbackSerial = CallbackSerial,
        string? verificationTokenValueId = null
    )
    {
        _libraries
            .GetLibraryByIdAsync(OrganizationId, LibraryId, Arg.Any<CancellationToken>())
            .Returns(
                new Library
                {
                    Id = LibraryId,
                    OrganizationId = OrganizationId,
                    TeamId = "team-1",
                    Name = "Notion",
                    SourceKind = RepositorySourceKind.Notion.ToString(),
                    ExternalSource = new LibraryExternalSource
                    {
                        Notion = new NotionSourceConfiguration
                        {
                            AccessTokenValueId = "enc-access",
                            VerificationTokenValueId = verificationTokenValueId,
                            WorkspaceId = "workspace-1",
                            CallbackTokenSerial = callbackSerial,
                        },
                    },
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                }
            );
    }

    private void ArrangeVerificationToken(string plaintext)
    {
        var now = DateTimeOffset.UtcNow;
        _encryptedValues
            .FindActiveAsync(OrganizationId, "enc-verification", Arg.Any<CancellationToken>())
            .Returns(
                new EncryptedValue
                {
                    Id = "enc-verification",
                    OrganizationId = OrganizationId,
                    Kind = EncryptedValueKind.SecretString,
                    EncryptionProvider = "test",
                    Ciphertext = Encoding.UTF8.GetBytes(plaintext),
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                }
            );
    }

    private static byte[] SignedEventBody() =>
        Encoding.UTF8.GetBytes(
            """
            {"id":"event-1","type":"page.created","timestamp":"2026-08-08T12:00:00Z","subscription_id":"subscription-1","workspace_id":"workspace-1","integration_id":"integration-1","attempt_number":1,"entity":{"id":"page-1","type":"page"}}
            """
        );

    private sealed class PassthroughEncryptionProvider : IDataEncryptionProvider
    {
        public string ProviderName => "test";

        public Task<byte[]> EncryptAsync(
            string organizationId,
            ReadOnlyMemory<byte> plaintext,
            CancellationToken cancellationToken
        ) => Task.FromResult(plaintext.ToArray());

        public Task<byte[]> DecryptAsync(
            string organizationId,
            ReadOnlyMemory<byte> ciphertext,
            CancellationToken cancellationToken
        ) => Task.FromResult(ciphertext.ToArray());
    }
}
