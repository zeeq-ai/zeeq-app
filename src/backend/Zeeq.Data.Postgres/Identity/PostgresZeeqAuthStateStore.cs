using Zeeq.Core.Identity;
using Zeeq.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Zeeq.Data.Postgres.Identity;

/// <summary>
/// PostgreSQL implementation of transient authentication state storage.
/// </summary>
public sealed class PostgresZeeqAuthStateStore(PostgresDbContext db) : IZeeqAuthStateStore
{
    /// <inheritdoc />
    public async Task StoreAsync(
        string purpose,
        string key,
        string payloadJson,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken
    )
    {
        db.AuthTransientStates.Add(
            new AuthTransientState
            {
                Purpose = purpose,
                Key = key,
                PayloadJson = payloadJson,
                ExpiresAtUtc = expiresAtUtc,
            }
        );

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<string?> ConsumeAsync(
        string purpose,
        string key,
        CancellationToken cancellationToken
    )
    {
        var now = DateTimeOffset.UtcNow;
        var updated = await db
            .AuthTransientStates.TagWithOperationCallSite("auth_state.consume_mark_consumed")
            .Where(state =>
                state.Purpose == purpose
                && state.Key == key
                && state.ConsumedAtUtc == null
                && state.ExpiresAtUtc > now
            )
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(state => state.ConsumedAtUtc, now),
                cancellationToken
            );

        if (updated != 1)
        {
            return null;
        }

        return await db
            .AuthTransientStates.TagWithOperationCallSite("auth_state.consume_read_payload")
            .AsNoTracking()
            .Where(state =>
                state.Purpose == purpose && state.Key == key && state.ConsumedAtUtc == now
            )
            .Select(state => state.PayloadJson)
            .SingleAsync(cancellationToken);
    }
}
