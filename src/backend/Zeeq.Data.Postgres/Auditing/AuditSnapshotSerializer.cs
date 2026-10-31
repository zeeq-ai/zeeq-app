using System.Text.Json;
using System.Text.RegularExpressions;
using Zeeq.Core.Auditing;

namespace Zeeq.Data.Postgres.Auditing;

/// <summary>
/// Serializes allowlisted snapshots to jsonb text and refuses field names that look like secrets.
/// </summary>
/// <remarks>
/// The name check is a backstop, not the allowlist: callers decide which fields are safe to record.
/// Secrets, ciphertext, and secret hashes must never reach this type.
/// </remarks>
internal static partial class AuditSnapshotSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    // `token` is allowed only as a count/id/name (e.g. TokenCount, UserTokenId).
    // NOTE: Optional separators reject both camelCase and snake_case secret field names.
    [GeneratedRegex(
        "secret|password|passwd|ciphertext|api[_-]?key|private[_-]?key|key[_-]?hash|bearer|authorization|(?<!user)token(?!_?(count|id|name|limit)s?\\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex SecretLikeName();

    public static string? Serialize(AuditSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        var size =
            (snapshot.Path?.Length ?? 0)
            + (snapshot.Title?.Length ?? 0)
            + (snapshot.Content?.Length ?? 0);

        foreach (var (name, value) in snapshot.Fields)
        {
            if (SecretLikeName().IsMatch(name))
            {
                throw new ArgumentException(
                    $"Audit snapshot field '{name}' looks like a secret and cannot be recorded.",
                    nameof(snapshot)
                );
            }

            size += name.Length + (value?.Length ?? 0);
        }

        if (size > AuditLimits.MaxSnapshotCharacters)
        {
            throw new ArgumentException(
                $"Audit snapshot exceeds {AuditLimits.MaxSnapshotCharacters} characters.",
                nameof(snapshot)
            );
        }

        return JsonSerializer.Serialize(snapshot, Options);
    }

    public static AuditSnapshot? Deserialize(string? json) =>
        json is null ? null : JsonSerializer.Deserialize<AuditSnapshot>(json, Options);
}
