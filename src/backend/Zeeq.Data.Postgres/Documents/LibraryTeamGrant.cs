namespace Zeeq.Data.Postgres.Documents;

internal sealed class LibraryTeamGrant
{
    public required string OrganizationId { get; init; }
    public required string LibraryId { get; init; }
    public required string TeamId { get; init; }
}
