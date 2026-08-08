using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Zeeq.Core.Documents;
using Zeeq.Core.Models;

namespace Zeeq.Data.Postgres.Documents;

/// <summary>
/// PostgreSQL implementation of Notion webhook state transitions.
/// </summary>
internal sealed class PostgresNotionWebhookStore(PostgresDbContext db) : INotionWebhookStore
{
    public async Task<NotionWebhookVerificationCaptureResult> TryCaptureVerificationTokenAsync(
        string organizationId,
        string libraryId,
        int callbackTokenSerial,
        EncryptedValue verificationToken,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken
    )
    {
        ValidateVerificationToken(organizationId, verificationToken);

        var (transaction, ownsTransaction) = await BeginTransactionIfNeededAsync(cancellationToken);
        await using var transactionScope = ownsTransaction ? transaction : null;

        var library = await LockNotionLibraryAsync(organizationId, libraryId, cancellationToken);
        var notion = library?.ExternalSource?.Notion;

        if (notion is null)
        {
            return NotionWebhookVerificationCaptureResult.NotFound;
        }

        if (notion.CallbackTokenSerial != callbackTokenSerial)
        {
            return NotionWebhookVerificationCaptureResult.StaleCallback;
        }

        if (notion.VerificationTokenValueId is not null)
        {
            return NotionWebhookVerificationCaptureResult.AlreadyStored;
        }

        db.EncryptedValues.Add(verificationToken);
        library!.ExternalSource = library.ExternalSource! with
        {
            Notion = notion with { VerificationTokenValueId = verificationToken.Id },
        };
        library.UpdatedAt = nowUtc;

        await db.SaveChangesAsync(cancellationToken);
        await CommitIfOwnedAsync(transaction, ownsTransaction, cancellationToken);

        return NotionWebhookVerificationCaptureResult.Stored;
    }

    public async Task<NotionWebhookEventObservationResult> ObserveSignedEventAsync(
        string organizationId,
        string libraryId,
        int callbackTokenSerial,
        string workspaceId,
        string subscriptionId,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);

        var (transaction, ownsTransaction) = await BeginTransactionIfNeededAsync(cancellationToken);
        await using var transactionScope = ownsTransaction ? transaction : null;

        var library = await LockNotionLibraryAsync(organizationId, libraryId, cancellationToken);
        var notion = library?.ExternalSource?.Notion;

        if (notion is null)
        {
            return NotionWebhookEventObservationResult.NotFound;
        }

        if (notion.CallbackTokenSerial != callbackTokenSerial)
        {
            return NotionWebhookEventObservationResult.StaleCallback;
        }

        if (notion.VerificationTokenValueId is null)
        {
            return NotionWebhookEventObservationResult.VerificationIncomplete;
        }

        if (
            !string.IsNullOrWhiteSpace(notion.WorkspaceId)
            && !string.Equals(notion.WorkspaceId, workspaceId, StringComparison.Ordinal)
        )
        {
            return NotionWebhookEventObservationResult.WorkspaceMismatch;
        }

        if (notion.WebhookSubscriptionId is not null)
        {
            return string.Equals(
                notion.WebhookSubscriptionId,
                subscriptionId,
                StringComparison.Ordinal
            )
                ? NotionWebhookEventObservationResult.Current
                : NotionWebhookEventObservationResult.SubscriptionMismatch;
        }

        library!.ExternalSource = library.ExternalSource! with
        {
            Notion = notion with
            {
                WebhookSubscriptionId = subscriptionId,
                WebhookActivatedAtUtc = observedAtUtc,
            },
        };
        library.UpdatedAt = observedAtUtc;

        await db.SaveChangesAsync(cancellationToken);
        await CommitIfOwnedAsync(transaction, ownsTransaction, cancellationToken);

        return NotionWebhookEventObservationResult.Activated;
    }

    public async Task<NotionWebhookResetResult> ResetAsync(
        string organizationId,
        string libraryId,
        DateTimeOffset resetAtUtc,
        CancellationToken cancellationToken
    )
    {
        var (transaction, ownsTransaction) = await BeginTransactionIfNeededAsync(cancellationToken);
        await using var transactionScope = ownsTransaction ? transaction : null;

        var library = await LockNotionLibraryAsync(organizationId, libraryId, cancellationToken);
        var notion = library?.ExternalSource?.Notion;

        if (notion is null)
        {
            return NotionWebhookResetResult.NotFound;
        }

        if (notion.VerificationTokenValueId is { } verificationTokenValueId)
        {
            var verificationToken = await db
                .EncryptedValues.TagWithOperationCallSite("notion_webhook.reset_token")
                .SingleOrDefaultAsync(
                    value =>
                        value.OrganizationId == organizationId
                        && value.Id == verificationTokenValueId
                        && value.DisabledAtUtc == null,
                    cancellationToken
                );

            if (verificationToken is not null)
            {
                verificationToken.DisabledAtUtc = resetAtUtc;
                verificationToken.UpdatedAtUtc = resetAtUtc;
            }
        }

        library!.ExternalSource = library.ExternalSource! with
        {
            Notion = notion with
            {
                VerificationTokenValueId = null,
                WebhookSubscriptionId = null,
                WebhookActivatedAtUtc = null,
                CallbackTokenSerial = notion.CallbackTokenSerial + 1,
            },
        };
        library.UpdatedAt = resetAtUtc;

        await db.SaveChangesAsync(cancellationToken);
        await CommitIfOwnedAsync(transaction, ownsTransaction, cancellationToken);

        return NotionWebhookResetResult.Reset;
    }

    private Task<Library?> LockNotionLibraryAsync(
        string organizationId,
        string libraryId,
        CancellationToken cancellationToken
    ) =>
        db
            .Libraries.FromSqlInterpolated(
                $"""
                SELECT *
                FROM zeeq.docs_libraries
                WHERE organization_id = {organizationId}
                  AND id = {libraryId}
                  AND source_kind = {RepositorySourceKind.Notion.ToString()}
                FOR UPDATE
                """
            )
            .TagWithOperationCallSite("notion_webhook.lock_library")
            .SingleOrDefaultAsync(cancellationToken);

    private static void ValidateVerificationToken(
        string organizationId,
        EncryptedValue verificationToken
    )
    {
        ArgumentNullException.ThrowIfNull(verificationToken);

        if (
            !string.Equals(
                verificationToken.OrganizationId,
                organizationId,
                StringComparison.Ordinal
            )
        )
        {
            throw new ArgumentException(
                "The verification token must belong to the callback organization.",
                nameof(verificationToken)
            );
        }

        if (verificationToken.Kind != EncryptedValueKind.SecretString)
        {
            throw new ArgumentException(
                "The verification token must be a secret string.",
                nameof(verificationToken)
            );
        }
    }

    private async Task<(
        IDbContextTransaction Transaction,
        bool OwnsTransaction
    )> BeginTransactionIfNeededAsync(CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is { } currentTransaction)
        {
            return (currentTransaction, false);
        }

        return (await db.Database.BeginTransactionAsync(cancellationToken), true);
    }

    private static Task CommitIfOwnedAsync(
        IDbContextTransaction transaction,
        bool ownsTransaction,
        CancellationToken cancellationToken
    ) => ownsTransaction ? transaction.CommitAsync(cancellationToken) : Task.CompletedTask;
}
