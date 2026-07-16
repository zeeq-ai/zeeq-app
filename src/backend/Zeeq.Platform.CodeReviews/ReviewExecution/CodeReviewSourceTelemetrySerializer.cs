using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Serializes and deserializes <see cref="CodeReviewSourceTelemetry" /> for the review record's
/// <c>SourceTelemetryPayload</c> jsonb column.
/// </summary>
/// <remarks>
/// One place owns the <see cref="JsonSerializerOptions" /> so the write path (runners) and the
/// read paths (comment handler, findings endpoint) stay in parity. Ignore-null trims optional
/// fields (e.g. a section snippet's <c>lang</c>) from the stored payload. Deserialization is
/// best-effort: a null, empty, <c>"{}"</c>, or malformed payload returns null rather than
/// throwing, so a bad row can never break rendering or the findings response.
///
/// <para>
/// <b>Why a manual serializer over EF 10 <c>ToJson()</c> / Npgsql POCO-as-jsonb mapping
/// (deferred).</b> The record column stays a plain <c>string</c> (jsonb) and we (de)serialize
/// here rather than mapping <see cref="CodeReviewSourceTelemetry" /> as an owned/complex JSON
/// navigation. Three reasons: (1) <b>layering</b> — the entity <c>CodeReviewRecord</c> lives in
/// <c>Zeeq.Core.Models</c> (no project references), so a typed JSON property would force this
/// compact-key storage shape + <c>schemaVersion</c> contract down into the domain layer, away
/// from the aggregator here in <c>Zeeq.Platform.CodeReviews</c>; (2) <b>resilience</b> — with
/// <c>ToJson()</c> the payload is deserialized during entity materialization, so a malformed,
/// legacy (<c>"{}"</c>), or <c>schemaVersion</c>-drifted payload throws for the <i>whole</i>
/// <c>CodeReviewRecord</c> load and breaks every hot read path (comment render, findings, list);
/// isolating the parse here degrades a bad row to "no sources panel" instead; (3) <b>the one
/// upside is out of scope</b> — <c>ToJson()</c>'s in-DB LINQ/<c>@&gt;</c> querying is exactly
/// what we chose not to do (analytics is an offline ETL over the raw payload — no GIN index, no
/// hot-table jsonb aggregation). Revisit <c>ToJson()</c> only if we later want to query telemetry
/// in SQL and accept relocating the type into <c>Zeeq.Core.Models</c>.
/// </para>
/// </remarks>
public static class CodeReviewSourceTelemetrySerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serializes a snapshot into the compact jsonb string stored on the review record.</summary>
    /// <param name="telemetry">The snapshot to serialize.</param>
    /// <returns>The compact JSON payload.</returns>
    public static string Serialize(CodeReviewSourceTelemetry telemetry) =>
        JsonSerializer.Serialize(telemetry, Options);

    /// <summary>Deserializes a stored payload, returning null for empty/<c>"{}"</c>/malformed input.</summary>
    /// <param name="payload">The stored jsonb payload, or null.</param>
    /// <returns>The parsed snapshot, or null when there is nothing usable to render.</returns>
    public static CodeReviewSourceTelemetry? Deserialize(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload) || payload == "{}")
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<CodeReviewSourceTelemetry>(payload, Options);
        }
        catch (JsonException)
        {
            // Best-effort: a malformed payload must never break rendering or the findings response.
            return null;
        }
    }
}
