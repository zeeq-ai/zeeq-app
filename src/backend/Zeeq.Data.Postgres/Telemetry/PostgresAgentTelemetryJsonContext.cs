using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zeeq.Data.Postgres.Telemetry;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(AgentSessionEventInsertRow[]))]
[JsonSerializable(typeof(AgentConversationRollupDelta[]))]
[JsonSerializable(typeof(AgentConversationRollupBackfillExcludedKey[]))]
internal sealed partial class PostgresAgentTelemetryJsonContext : JsonSerializerContext;
