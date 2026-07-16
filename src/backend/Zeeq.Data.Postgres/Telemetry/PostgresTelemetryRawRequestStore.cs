using System.Data;
using Zeeq.Core.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Zeeq.Data.Postgres.Telemetry;

/// <summary>
/// Postgres implementation of <see cref="ITelemetryRawRequestStore"/> using an unlogged
/// table with lease-based claim semantics.
/// </summary>
/// <remarks>
/// The raw table is <c>UNLOGGED</c> — rows survive process crashes but are lost on
/// database restart/failover (acceptable for best-effort telemetry). Lease-based
/// claim operations use raw SQL with <c>xmax = 0</c> tricks to atomically claim
/// rows in a single round trip.
///
/// Acknowledge semantics: only the current lease-holder can delete or quarantine
/// a row. An expired lease is reclaimable by any processor — the same atomic
/// claim pattern applies.
/// </remarks>
internal sealed class PostgresTelemetryRawRequestStore(PostgresDbContext db)
    : ITelemetryRawRequestStore
{
    /// <inheritdoc />
    public Task<string> StoreLogsAsync(
        byte[] payload,
        TelemetrySignalType signalType,
        TelemetryRawRequestMetadata metadata,
        string ingestUserId,
        string ingestOrganizationId,
        CancellationToken cancellationToken = default
    ) =>
        StoreAsync(
            payload,
            signalType,
            metadata,
            ingestUserId,
            ingestOrganizationId,
            cancellationToken
        );

    /// <inheritdoc />
    public Task<string> StoreTracesAsync(
        byte[] payload,
        TelemetrySignalType signalType,
        TelemetryRawRequestMetadata metadata,
        string ingestUserId,
        string ingestOrganizationId,
        CancellationToken cancellationToken = default
    ) =>
        StoreAsync(
            payload,
            signalType,
            metadata,
            ingestUserId,
            ingestOrganizationId,
            cancellationToken
        );

    private async Task<string> StoreAsync(
        byte[] payload,
        TelemetrySignalType signalType,
        TelemetryRawRequestMetadata metadata,
        string ingestUserId,
        string ingestOrganizationId,
        CancellationToken cancellationToken = default
    )
    {
        var row = new TelemetryRawRequest
        {
            Id = "telr_" + Guid.CreateVersion7().ToString("N"),
            ReceivedAtUtc = DateTimeOffset.UtcNow,
            SignalType = signalType,
            IngestUserId = ingestUserId,
            IngestOrganizationId = ingestOrganizationId,
            SourceName = metadata.Source,
            HarnessName = metadata.Harness,
            RecordCount = metadata.RecordCount,
            Payload = payload,
        };

        db.Set<TelemetryRawRequest>().Add(row);
        await db.SaveChangesAsync(cancellationToken);

        return row.Id;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TelemetryRawRequest>> ClaimBatchAsync(
        int batchSize,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default
    )
    {
        var leaseId = "cls_" + Guid.CreateVersion7().ToString("N");
        var expiresAt = DateTimeOffset.UtcNow.Add(leaseDuration);
        var now = DateTimeOffset.UtcNow;

        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var cmd = connection.CreateCommand();
        // NOTE: The CTE with FOR UPDATE SKIP LOCKED on the SELECT id clause
        // locks the selected rows within the implicit read-committed transaction
        // held by the open NpgsqlConnection. The UPDATE ... FROM claimed WHERE id
        // = claimed.id operates on those same locked rows. The lock is released
        // when the connection is returned to the pool. This is a standard Postgres
        // claim-and-update pattern.
        cmd.CommandText = """
            WITH claimed AS (
                SELECT id
                FROM zeeq.telemetry_raw_requests
                WHERE processing_status = 'Pending'
                   OR (processing_status = 'Processing' AND processing_lease_expires_at_utc < @now)
                ORDER BY received_at_utc
                LIMIT @batch_size
                FOR UPDATE SKIP LOCKED
            )
            UPDATE zeeq.telemetry_raw_requests
            SET processing_status = 'Processing',
                processing_lease_id = @lease_id,
                processing_lease_expires_at_utc = @expires_at,
                attempt_count = attempt_count + 1
            FROM claimed
            WHERE telemetry_raw_requests.id = claimed.id
            RETURNING
                telemetry_raw_requests.id,
                telemetry_raw_requests.received_at_utc,
                telemetry_raw_requests.signal_type,
                telemetry_raw_requests.ingest_user_id,
                telemetry_raw_requests.ingest_organization_id,
                telemetry_raw_requests.source_name,
                telemetry_raw_requests.harness_name,
                telemetry_raw_requests.record_count,
                telemetry_raw_requests.payload,
                telemetry_raw_requests.processing_status,
                telemetry_raw_requests.processing_lease_id,
                telemetry_raw_requests.effective_obfuscation_policy_version,
                telemetry_raw_requests.attempt_count,
                telemetry_raw_requests.quarantine_reason,
                telemetry_raw_requests.quarantined_at_utc
            """;

        cmd.Parameters.Add(new NpgsqlParameter("batch_size", batchSize));
        cmd.Parameters.Add(new NpgsqlParameter("lease_id", leaseId));
        cmd.Parameters.Add(new NpgsqlParameter("expires_at", expiresAt));
        cmd.Parameters.Add(new NpgsqlParameter("now", now));

        var rows = new List<TelemetryRawRequest>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(
                new TelemetryRawRequest
                {
                    Id = reader.GetString(0),
                    ReceivedAtUtc = reader.GetDateTime(1),
                    SignalType = Enum.Parse<TelemetrySignalType>(reader.GetString(2)),
                    IngestUserId = reader.IsDBNull(3) ? null : reader.GetString(3),
                    IngestOrganizationId = reader.IsDBNull(4) ? null : reader.GetString(4),
                    SourceName = reader.IsDBNull(5) ? null : reader.GetString(5),
                    HarnessName = reader.IsDBNull(6) ? null : reader.GetString(6),
                    RecordCount = reader.GetInt32(7),
                    Payload = (byte[])reader.GetValue(8),
                    ProcessingStatus = Enum.Parse<TelemetryRawRequestProcessingStatus>(
                        reader.GetString(9)
                    ),
                    ProcessingLeaseId = reader.IsDBNull(10) ? null : reader.GetString(10),
                    EffectiveObfuscationPolicyVersion = reader.GetInt32(11),
                    AttemptCount = reader.GetInt32(12),
                    QuarantineReason = reader.IsDBNull(13) ? null : reader.GetString(13),
                    QuarantinedAtUtc = reader.IsDBNull(14) ? null : reader.GetDateTime(14),
                }
            );
        }

        return rows;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteClaimedAsync(
        string rawRequestId,
        string processingLeaseId,
        CancellationToken cancellationToken = default
    )
    {
        var deleted = await db.Set<TelemetryRawRequest>()
            .Where(r => r.Id == rawRequestId && r.ProcessingLeaseId == processingLeaseId)
            .ExecuteDeleteAsync(cancellationToken);

        return deleted > 0;
    }

    /// <inheritdoc />
    public async Task<int> ReleaseExpiredLeasesAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        var released = await db.Set<TelemetryRawRequest>()
            .Where(r =>
                r.ProcessingStatus == TelemetryRawRequestProcessingStatus.Processing
                && r.ProcessingLeaseExpiresAtUtc < now
            )
            .ExecuteUpdateAsync(
                setter =>
                    setter
                        .SetProperty(
                            r => r.ProcessingStatus,
                            TelemetryRawRequestProcessingStatus.Pending
                        )
                        .SetProperty(r => r.ProcessingLeaseId, r => null)
                        .SetProperty(r => r.ProcessingLeaseExpiresAtUtc, r => null),
                cancellationToken
            );

        return released;
    }

    /// <inheritdoc />
    public async Task QuarantineAsync(
        string rawRequestId,
        string processingLeaseId,
        string reason,
        CancellationToken cancellationToken = default
    )
    {
        var now = DateTimeOffset.UtcNow;

        await db.Set<TelemetryRawRequest>()
            .Where(r => r.Id == rawRequestId && r.ProcessingLeaseId == processingLeaseId)
            .ExecuteUpdateAsync(
                setter =>
                    setter
                        .SetProperty(
                            r => r.ProcessingStatus,
                            TelemetryRawRequestProcessingStatus.Quarantined
                        )
                        .SetProperty(r => r.QuarantineReason, reason)
                        .SetProperty(r => r.QuarantinedAtUtc, now),
                cancellationToken
            );
    }

    /// <inheritdoc />
    public async Task RecordProcessingFailureAsync(
        string rawRequestId,
        string processingLeaseId,
        Exception error,
        CancellationToken cancellationToken = default
    )
    {
        await db.Set<TelemetryRawRequest>()
            .Where(r => r.Id == rawRequestId && r.ProcessingLeaseId == processingLeaseId)
            .ExecuteUpdateAsync(
                setter =>
                    setter
                        .SetProperty(
                            r => r.ProcessingStatus,
                            TelemetryRawRequestProcessingStatus.Pending
                        )
                        .SetProperty(r => r.ProcessingLeaseId, r => null)
                        .SetProperty(r => r.ProcessingLeaseExpiresAtUtc, r => null),
                cancellationToken
            );
    }
}
