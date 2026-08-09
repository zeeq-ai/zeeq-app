using Microsoft.Extensions.Logging;
using Zeeq.Core.Documents;
using Zeeq.Core.Models;
using Zeeq.Core.Security;
using Zeeq.Integrations.Notion;

namespace Zeeq.Platform.Ingest;

/// <summary>Resolves the current encrypted Notion token and runs the job in process.</summary>
public sealed partial class NotionIngestDispatcher(
    ILibraryDocumentStore libraries,
    IEncryptedValueStore encryptedValues,
    EncryptedValueEncryptionService encryption,
    IZeeqNotionClientFactory clients,
    NotionIngestRunner runner,
    IDocsIngestRunStore runStore,
    ILogger<NotionIngestDispatcher> logger
) : INotionIngestDispatcher
{
    /// <inheritdoc />
    public async Task<NotionDispatchOutcome> RunAsync(
        NotionIngestJob job,
        CancellationToken cancellationToken
    )
    {
        IZeeqNotionClient client;
        try
        {
            var resolution = await ResolveClientAsync(job, cancellationToken);
            if (resolution is NotionCredentialResolution.Failed failure)
            {
                await RecordFatalFailureAsync(
                    job,
                    failure.Reason,
                    authFailure: true,
                    cancellationToken
                );
                return new NotionDispatchOutcome(IngestRunStatus.Failed, failure.Reason);
            }

            client = ((NotionCredentialResolution.Resolved)resolution).Client;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogCredentialResolutionFailed(logger, job.RunId, job.LibraryId, ex);
            await RecordFatalFailureAsync(job, ex.Message, authFailure: false, cancellationToken);
            return new NotionDispatchOutcome(IngestRunStatus.Failed, ex.Message);
        }

        try
        {
            using (client)
            {
                var run = await runner.RunAsync(job, client, cancellationToken);
                return new NotionDispatchOutcome(run.Status, run.FailureMessage);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogRunFailed(logger, job.RunId, job.LibraryId, ex);
            await RecordFatalFailureAsync(job, ex.Message, authFailure: false, cancellationToken);
            return new NotionDispatchOutcome(IngestRunStatus.Failed, ex.Message);
        }
    }

    private async Task<NotionCredentialResolution> ResolveClientAsync(
        NotionIngestJob job,
        CancellationToken cancellationToken
    )
    {
        var library = await libraries.GetLibraryByIdAsync(
            job.OrganizationId,
            job.LibraryId,
            cancellationToken
        );
        var tokenId = library?.ExternalSource?.Notion?.AccessTokenValueId;
        if (
            library?.SourceKind != RepositorySourceKind.Notion.ToString()
            || string.IsNullOrWhiteSpace(tokenId)
        )
        {
            return new NotionCredentialResolution.Failed(
                "The library no longer has an active Notion source configuration."
            );
        }

        var encryptedToken = await encryptedValues.FindActiveAsync(
            job.OrganizationId,
            tokenId,
            cancellationToken
        );
        if (encryptedToken is null)
        {
            return new NotionCredentialResolution.Failed("The Notion access token is unavailable.");
        }

        var accessToken = await encryption.DecryptAsync(
            encryptedToken,
            EncryptedValueKind.SecretString,
            cancellationToken
        );
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return new NotionCredentialResolution.Failed(
                "The Notion access token could not be read."
            );
        }

        return new NotionCredentialResolution.Resolved(clients.Create(accessToken));
    }

    private async Task RecordFatalFailureAsync(
        NotionIngestJob job,
        string failureMessage,
        bool authFailure,
        CancellationToken cancellationToken
    )
    {
        var now = DateTimeOffset.UtcNow;
        try
        {
            await runStore.CreateAsync(
                new DocsIngestRun
                {
                    Id = job.RunId,
                    CreatedAtUtc = job.RunCreatedAtUtc,
                    SourceKind = RepositorySourceKind.Notion,
                    RepoUrl = job.SourceReference,
                    OrganizationId = job.OrganizationId,
                    LibraryId = job.LibraryId,
                    Trigger = job.Trigger,
                    SyncScope = job.Scope,
                    Status = IngestRunStatus.Running,
                    StartedAtUtc = now,
                    UpdatedAtUtc = now,
                },
                cancellationToken
            );
        }
        catch (Exception)
        {
            // The runner may already have created the run. Finalization below is idempotent by key.
        }

        try
        {
            await runStore.FinalizeAsync(
                job.RunId,
                job.RunCreatedAtUtc,
                new IngestRunFinalization(
                    Status: IngestRunStatus.Failed,
                    FilesTotal: 0,
                    FilesAdded: 0,
                    FilesUpdated: 0,
                    FilesMoved: 0,
                    FilesSkipped: 0,
                    FilesDeleted: 0,
                    FilesFailed: 0,
                    AuthFailure: authFailure,
                    FailureMessage: failureMessage,
                    CompletedAtUtc: now
                ),
                cancellationToken
            );
        }
        catch (Exception finalizeEx)
        {
            LogFailureRecordingFailed(logger, job.RunId, finalizeEx);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Notion credential resolution failed. RunId={RunId}, LibraryId={LibraryId}"
    )]
    private static partial void LogCredentialResolutionFailed(
        ILogger logger,
        string runId,
        string libraryId,
        Exception ex
    );

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Notion ingest run threw unexpectedly. RunId={RunId}, LibraryId={LibraryId}"
    )]
    private static partial void LogRunFailed(
        ILogger logger,
        string runId,
        string libraryId,
        Exception ex
    );

    [LoggerMessage(
        Level = LogLevel.Critical,
        Message = "Failed to record a fatal Notion ingest failure. RunId={RunId}"
    )]
    private static partial void LogFailureRecordingFailed(
        ILogger logger,
        string runId,
        Exception ex
    );

    private abstract record NotionCredentialResolution
    {
        internal sealed record Resolved(IZeeqNotionClient Client) : NotionCredentialResolution;

        internal sealed record Failed(string Reason) : NotionCredentialResolution;
    }
}
