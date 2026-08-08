using Microsoft.EntityFrameworkCore;
using Zeeq.Core.Documents;
using Zeeq.Core.Models;
using Zeeq.Data.Postgres.Documents;
using Zeeq.Testing;
using Zeeq.Testing.EntityGraphs;

namespace Zeeq.Data.Postgres.Tests;

/// <summary>
/// Integration tests for atomic Notion webhook verification and activation state transitions.
///
/// Run:
/// dotnet run --project src/backend/Zeeq.Data.Postgres.Tests --output detailed --disable-logo --treenode-filter "/*/*/PostgresNotionWebhookStoreTests/*"
/// </summary>
[Property("integration", "true")]
[Property("testcontainer", "true")]
[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class PostgresNotionWebhookStoreTests : PgTransactionalTestBase
{
    private readonly PgDatabaseFixture _postgres;

    public PostgresNotionWebhookStoreTests(PgDatabaseFixture postgres)
        : base(postgres)
    {
        _postgres = postgres;
    }

    [Test]
    public async Task TryCaptureVerificationTokenAsync_PersistsTokenAndLibraryReferenceTogether()
    {
        var (store, organizationId, libraryId) = await CreateStoreAndLibraryAsync(_context);
        var token = NewEncryptedToken(organizationId);

        var result = await store.TryCaptureVerificationTokenAsync(
            organizationId,
            libraryId,
            callbackTokenSerial: 3,
            token,
            DateTimeOffset.UtcNow,
            CancellationToken.None
        );

        _context.ChangeTracker.Clear();
        var library = await _context.Libraries.SingleAsync(row =>
            row.OrganizationId == organizationId && row.Id == libraryId
        );
        var persistedToken = await _context.EncryptedValues.SingleOrDefaultAsync(row =>
            row.OrganizationId == organizationId && row.Id == token.Id
        );

        await Assert.That(result).IsEqualTo(NotionWebhookVerificationCaptureResult.Stored);
        await Assert.That(persistedToken).IsNotNull();
        await Assert
            .That(library.ExternalSource!.Notion!.VerificationTokenValueId)
            .IsEqualTo(token.Id);
    }

    [Test]
    public async Task TryCaptureVerificationTokenAsync_RejectsStaleCallbackWithoutPersistingToken()
    {
        var (store, organizationId, libraryId) = await CreateStoreAndLibraryAsync(_context);
        var token = NewEncryptedToken(organizationId);

        var result = await store.TryCaptureVerificationTokenAsync(
            organizationId,
            libraryId,
            callbackTokenSerial: 2,
            token,
            DateTimeOffset.UtcNow,
            CancellationToken.None
        );

        var persisted = await _context.EncryptedValues.AnyAsync(row =>
            row.OrganizationId == organizationId && row.Id == token.Id
        );

        await Assert.That(result).IsEqualTo(NotionWebhookVerificationCaptureResult.StaleCallback);
        await Assert.That(persisted).IsFalse();
    }

    [Test]
    public async Task TryCaptureVerificationTokenAsync_AllowsOnlyOneConcurrentFirstWriter()
    {
        string organizationId;
        string libraryId;

        await using (var seedContext = _postgres.CreateContext())
        {
            (_, organizationId, libraryId) = await CreateStoreAndLibraryAsync(seedContext);
        }

        var firstToken = NewEncryptedToken(organizationId);
        var secondToken = NewEncryptedToken(organizationId);

        await using var firstContext = _postgres.CreateContext();
        await using var secondContext = _postgres.CreateContext();
        var firstStore = new PostgresNotionWebhookStore(firstContext);
        var secondStore = new PostgresNotionWebhookStore(secondContext);

        var results = await Task.WhenAll(
            firstStore.TryCaptureVerificationTokenAsync(
                organizationId,
                libraryId,
                3,
                firstToken,
                DateTimeOffset.UtcNow,
                CancellationToken.None
            ),
            secondStore.TryCaptureVerificationTokenAsync(
                organizationId,
                libraryId,
                3,
                secondToken,
                DateTimeOffset.UtcNow,
                CancellationToken.None
            )
        );

        await using var assertionContext = _postgres.CreateContext();
        var library = await assertionContext.Libraries.SingleAsync(row =>
            row.OrganizationId == organizationId && row.Id == libraryId
        );
        var tokenIds = new[] { firstToken.Id, secondToken.Id };
        var persistedTokenIds = await assertionContext
            .EncryptedValues.Where(row =>
                row.OrganizationId == organizationId && tokenIds.Contains(row.Id)
            )
            .Select(row => row.Id)
            .ToArrayAsync();

        await Assert
            .That(results.Count(result => result == NotionWebhookVerificationCaptureResult.Stored))
            .IsEqualTo(1);
        await Assert
            .That(
                results.Count(result =>
                    result == NotionWebhookVerificationCaptureResult.AlreadyStored
                )
            )
            .IsEqualTo(1);
        await Assert.That(persistedTokenIds).HasSingleItem();
        await Assert
            .That(library.ExternalSource!.Notion!.VerificationTokenValueId)
            .IsEqualTo(persistedTokenIds[0]);
    }

    [Test]
    public async Task ObserveSignedEventAsync_ActivatesOnceAndRejectsDifferentSubscription()
    {
        var (store, organizationId, libraryId) = await CreateStoreAndLibraryAsync(
            _context,
            verificationTokenValueId: "enc_verification"
        );
        var activatedAt = DateTimeOffset.UtcNow;

        var activated = await store.ObserveSignedEventAsync(
            organizationId,
            libraryId,
            3,
            "workspace-1",
            "subscription-1",
            activatedAt,
            CancellationToken.None
        );
        var current = await store.ObserveSignedEventAsync(
            organizationId,
            libraryId,
            3,
            "workspace-1",
            "subscription-1",
            activatedAt.AddSeconds(1),
            CancellationToken.None
        );
        var mismatch = await store.ObserveSignedEventAsync(
            organizationId,
            libraryId,
            3,
            "workspace-1",
            "subscription-2",
            activatedAt.AddSeconds(2),
            CancellationToken.None
        );

        _context.ChangeTracker.Clear();
        var library = await _context.Libraries.SingleAsync(row =>
            row.OrganizationId == organizationId && row.Id == libraryId
        );

        await Assert.That(activated).IsEqualTo(NotionWebhookEventObservationResult.Activated);
        await Assert.That(current).IsEqualTo(NotionWebhookEventObservationResult.Current);
        await Assert
            .That(mismatch)
            .IsEqualTo(NotionWebhookEventObservationResult.SubscriptionMismatch);
        await Assert
            .That(library.ExternalSource!.Notion!.WebhookSubscriptionId)
            .IsEqualTo("subscription-1");
        await Assert
            .That(library.ExternalSource.Notion.WebhookActivatedAtUtc)
            .IsEqualTo(activatedAt.TruncateToPostgresPrecision());
    }

    private static async Task<(
        PostgresNotionWebhookStore Store,
        string OrganizationId,
        string LibraryId
    )> CreateStoreAndLibraryAsync(
        PostgresDbContext context,
        string? verificationTokenValueId = null
    )
    {
        var seed = await EntityGraph.AddGeneratedSeed(context).BuildAsync();
        var now = DateTimeOffset.UtcNow;
        var library = new Library
        {
            Id = SeedContext.NewId("library"),
            OrganizationId = seed.Organization.Id,
            Name = SeedContext.NewId("notion-library"),
            SourceKind = RepositorySourceKind.Notion.ToString(),
            SyncStatus = "idle",
            ExternalSource = new LibraryExternalSource
            {
                Notion = new NotionSourceConfiguration
                {
                    ConnectionName = "Engineering Docs",
                    WorkspaceId = "workspace-1",
                    AccessTokenValueId = "enc_access",
                    VerificationTokenValueId = verificationTokenValueId,
                    CallbackTokenSerial = 3,
                },
            },
            CreatedAt = now,
            UpdatedAt = now,
        };

        context.Libraries.Add(library);
        await context.SaveChangesAsync();

        return (new PostgresNotionWebhookStore(context), seed.Organization.Id, library.Id);
    }

    private static EncryptedValue NewEncryptedToken(string organizationId)
    {
        var now = DateTimeOffset.UtcNow;

        return new EncryptedValue
        {
            Id = SeedContext.NewId("encrypted-value"),
            OrganizationId = organizationId,
            Kind = EncryptedValueKind.SecretString,
            EncryptionProvider = "test",
            Name = "Notion webhook verification token",
            Ciphertext = [1, 2, 3],
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
    }
}
