using System.Text.Json.Serialization;

namespace Zeeq.Integrations.Notion;

/// <summary>Source-generated JSON metadata for Notion webhook contracts.</summary>
[JsonSerializable(typeof(NotionWebhookEvent))]
[JsonSerializable(typeof(NotionCallbackPayload))]
internal sealed partial class NotionWebhookJsonContext : JsonSerializerContext;
