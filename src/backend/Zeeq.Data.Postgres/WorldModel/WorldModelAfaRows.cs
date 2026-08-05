using System.Text.Json;
using Zeeq.Platform.WorldModel.Afa;

namespace Zeeq.Data.Postgres.WorldModel;

internal sealed class WorldModelNodeRow
{
    public Guid Id { get; set; }
    public required string OrganizationId { get; set; }
    public string? TeamId { get; set; }
    public Guid? ParentId { get; set; }
    public WorldModelNodeKind Kind { get; set; }
    public required string Segment { get; set; }
    public required string Path { get; set; }
    public string? Description { get; set; }
    public JsonDocument? Obsolete { get; set; }
    public bool IsEffectivelyObsolete { get; set; }
    public long Version { get; set; }
    public long SemanticRevision { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

internal sealed class WorldModelBodyItemRow
{
    public Guid Id { get; set; }
    public required string OrganizationId { get; set; }
    public Guid NodeId { get; set; }
    public WorldModelBodyKind Kind { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public required string Content { get; set; }
    public string[] Participants { get; set; } = [];
    public JsonDocument? Obsolete { get; set; }
    public long Revision { get; set; }
    public string? RepoPrSha { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
