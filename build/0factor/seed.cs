#:property TargetFramework=net10.0
#:property ImplicitUsings=enable
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:property PublishAot=false
#:project ../../src/backend/Zeeq.Data.Postgres/Zeeq.Data.Postgres.csproj
#:package Zeeq.Biome@1.1.0

using System.Net;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zeeq.Biome;
using Zeeq.Core.Models;
using Zeeq.Data.Postgres;

if (
    !string.Equals(
        Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"),
        "Development",
        StringComparison.OrdinalIgnoreCase
    )
)
{
    throw new InvalidOperationException("The 0factor fixture can only be seeded in Development.");
}

var rawConnectionString =
    Environment.GetEnvironmentVariable("ConnectionStrings__zeeq")
    ?? throw new InvalidOperationException(
        "ConnectionStrings__zeeq must be supplied by the local Aspire resource."
    );
if (string.IsNullOrWhiteSpace(rawConnectionString))
{
    throw new InvalidOperationException(
        "ConnectionStrings__zeeq must be supplied by the local Aspire resource."
    );
}

var connection = new NpgsqlConnectionStringBuilder(rawConnectionString);

if (
    !IsLoopbackHost(connection.Host)
    || !string.Equals(connection.Database, "zeeq", StringComparison.OrdinalIgnoreCase)
)
{
    throw new InvalidOperationException(
        "The 0factor fixture can only connect to the local zeeq development database."
    );
}

var dbOptions = new DbContextOptionsBuilder<PostgresDbContext>()
    .UseNpgsql(
        connection.ConnectionString,
        options =>
        {
            options.SetPostgresVersion(18, 0);
            options.UseVector();
        }
    )
    .UseOpenIddict()
    .UseSnakeCaseNamingConvention()
    .EnableSensitiveDataLogging(false)
    .Options;

await using var db = new PostgresDbContext(dbOptions);

var personas = new[]
{
    new PersonaFixture(
        "usr_0factor_e2e_alice",
        "e2e-alice-owner",
        "Alice Owner",
        "alice@e2e.example.test"
    ),
    new PersonaFixture("usr_0factor_e2e_bob", "e2e-bob-multi", "Bob Multi", "bob@e2e.example.test"),
    new PersonaFixture(
        "usr_0factor_e2e_cara",
        "e2e-cara-research",
        "Cara Research",
        "cara@e2e.example.test"
    ),
    new PersonaFixture(
        "usr_0factor_e2e_erin",
        "e2e-erin-outsider",
        "Erin Outsider",
        "erin@e2e.example.test"
    ),
};

var organizationRows = new[]
{
    new OrganizationFixture(
        "org_0factor_e2e_alpha",
        "Alpha",
        "alpha-0factor-e2e",
        "usr_0factor_e2e_alice"
    ),
    new OrganizationFixture(
        "org_0factor_e2e_beta",
        "Beta",
        "beta-0factor-e2e",
        "usr_0factor_e2e_bob"
    ),
};

var teamRows = new[]
{
    new TeamFixture(
        "team_0factor_e2e_alpha_root",
        "org_0factor_e2e_alpha",
        "Alpha Root",
        true,
        "usr_0factor_e2e_alice"
    ),
    new TeamFixture(
        "team_0factor_e2e_alpha_engineering",
        "org_0factor_e2e_alpha",
        "Engineering",
        false,
        "usr_0factor_e2e_alice"
    ),
    new TeamFixture(
        "team_0factor_e2e_alpha_research",
        "org_0factor_e2e_alpha",
        "Research",
        false,
        "usr_0factor_e2e_alice"
    ),
    new TeamFixture(
        "team_0factor_e2e_beta_root",
        "org_0factor_e2e_beta",
        "Beta Root",
        true,
        "usr_0factor_e2e_bob"
    ),
};

var organizationMembershipRows = new[]
{
    new OrganizationMembershipFixture(
        "mem_0factor_e2e_alpha_alice",
        "org_0factor_e2e_alpha",
        "usr_0factor_e2e_alice",
        "owner",
        true,
        "usr_0factor_e2e_alice"
    ),
    new OrganizationMembershipFixture(
        "mem_0factor_e2e_alpha_bob",
        "org_0factor_e2e_alpha",
        "usr_0factor_e2e_bob",
        "member",
        false,
        "usr_0factor_e2e_alice"
    ),
    new OrganizationMembershipFixture(
        "mem_0factor_e2e_alpha_cara",
        "org_0factor_e2e_alpha",
        "usr_0factor_e2e_cara",
        "member",
        true,
        "usr_0factor_e2e_alice"
    ),
    new OrganizationMembershipFixture(
        "mem_0factor_e2e_beta_alice",
        "org_0factor_e2e_beta",
        "usr_0factor_e2e_alice",
        "member",
        false,
        "usr_0factor_e2e_bob"
    ),
    new OrganizationMembershipFixture(
        "mem_0factor_e2e_beta_bob",
        "org_0factor_e2e_beta",
        "usr_0factor_e2e_bob",
        "admin",
        true,
        "usr_0factor_e2e_bob"
    ),
    new OrganizationMembershipFixture(
        "mem_0factor_e2e_beta_erin",
        "org_0factor_e2e_beta",
        "usr_0factor_e2e_erin",
        "member",
        true,
        "usr_0factor_e2e_bob"
    ),
    new OrganizationMembershipFixture(
        "inv_0factor_e2e_dana_alpha",
        "org_0factor_e2e_alpha",
        null,
        "member",
        false,
        "usr_0factor_e2e_alice",
        "dana@e2e.example.test",
        MembershipStatus.Pending
    ),
};

var teamMembershipRows = new[]
{
    new TeamMembershipFixture(
        "org_0factor_e2e_alpha",
        "team_0factor_e2e_alpha_root",
        "usr_0factor_e2e_alice",
        "owner",
        "usr_0factor_e2e_alice"
    ),
    new TeamMembershipFixture(
        "org_0factor_e2e_alpha",
        "team_0factor_e2e_alpha_root",
        "usr_0factor_e2e_bob",
        "member",
        "usr_0factor_e2e_alice"
    ),
    new TeamMembershipFixture(
        "org_0factor_e2e_alpha",
        "team_0factor_e2e_alpha_root",
        "usr_0factor_e2e_cara",
        "member",
        "usr_0factor_e2e_alice"
    ),
    new TeamMembershipFixture(
        "org_0factor_e2e_alpha",
        "team_0factor_e2e_alpha_engineering",
        "usr_0factor_e2e_alice",
        "admin",
        "usr_0factor_e2e_alice"
    ),
    new TeamMembershipFixture(
        "org_0factor_e2e_alpha",
        "team_0factor_e2e_alpha_engineering",
        "usr_0factor_e2e_bob",
        "member",
        "usr_0factor_e2e_alice"
    ),
    new TeamMembershipFixture(
        "org_0factor_e2e_alpha",
        "team_0factor_e2e_alpha_research",
        "usr_0factor_e2e_cara",
        "member",
        "usr_0factor_e2e_alice"
    ),
    new TeamMembershipFixture(
        "org_0factor_e2e_beta",
        "team_0factor_e2e_beta_root",
        "usr_0factor_e2e_alice",
        "member",
        "usr_0factor_e2e_bob"
    ),
    new TeamMembershipFixture(
        "org_0factor_e2e_beta",
        "team_0factor_e2e_beta_root",
        "usr_0factor_e2e_bob",
        "admin",
        "usr_0factor_e2e_bob"
    ),
    new TeamMembershipFixture(
        "org_0factor_e2e_beta",
        "team_0factor_e2e_beta_root",
        "usr_0factor_e2e_erin",
        "member",
        "usr_0factor_e2e_bob"
    ),
};

var biomeOptions = new BiomeOptions<FixtureSeed>()
    .SeedEntities(_ => Array.Empty<object>())
    .TimePrecision(TimeSpan.FromTicks(10));

await Biome
    .Start(new FixtureSeed(), biomeOptions, new FixtureStore(db))
    .Add(
        Blueprint.For<FixtureSeed, User>(context =>
        {
            var row = personas[context.Index];
            return new User
            {
                Id = row.UserId,
                DisplayName = row.DisplayName,
                Email = row.Email,
                EmailVerified = true,
                CreatedAtUtc = context.Now,
                UpdatedAtUtc = context.Now,
            };
        }),
        out _,
        personas.Length
    )
    .Add(
        Blueprint.For<FixtureSeed, ExternalUserIdentity>(context =>
        {
            var row = personas[context.Index];
            return new ExternalUserIdentity
            {
                UserId = row.UserId,
                Provider = "mock",
                ProviderSubject = row.Subject,
                DisplayName = row.DisplayName,
                Email = row.Email,
                EmailVerified = true,
                FirstSeenAtUtc = context.Now,
                LastSeenAtUtc = context.Now,
            };
        }),
        out _,
        personas.Length
    )
    .Add(
        Blueprint.For<FixtureSeed, Organization>(context =>
        {
            var row = organizationRows[context.Index];
            return new Organization
            {
                Id = row.Id,
                DisplayName = row.DisplayName,
                Slug = row.Slug,
                CreatedByUserId = row.CreatedByUserId,
                CreatedAtUtc = context.Now,
                UpdatedAtUtc = context.Now,
                ActivatedAtUtc = context.Now,
            };
        }),
        out _,
        organizationRows.Length
    )
    .Add(
        Blueprint.For<FixtureSeed, Team>(context =>
        {
            var row = teamRows[context.Index];
            return new Team
            {
                Id = row.Id,
                OrganizationId = row.OrganizationId,
                DisplayName = row.DisplayName,
                IsRootTeam = row.IsRootTeam,
                CreatedByUserId = row.CreatedByUserId,
                CreatedAtUtc = context.Now,
                UpdatedAtUtc = context.Now,
            };
        }),
        out _,
        teamRows.Length
    )
    .Add(
        Blueprint.For<FixtureSeed, OrganizationMembership>(context =>
        {
            var row = organizationMembershipRows[context.Index];
            return new OrganizationMembership
            {
                Id = row.Id,
                OrganizationId = row.OrganizationId,
                UserId = row.UserId,
                Role = row.Role,
                Status = row.Status,
                InvitedEmail = row.InvitedEmail,
                IsSameDomainAutoInvite = false,
                IsDefault = row.IsDefault,
                CreatedByUserId = row.CreatedByUserId,
                CreatedAtUtc = context.Now,
                ExpiresAtUtc =
                    row.Status == MembershipStatus.Pending ? context.Now.AddDays(7) : null,
            };
        }),
        out _,
        organizationMembershipRows.Length
    )
    .Add(
        Blueprint.For<FixtureSeed, TeamMembership>(context =>
        {
            var row = teamMembershipRows[context.Index];
            return new TeamMembership
            {
                OrganizationId = row.OrganizationId,
                TeamId = row.TeamId,
                UserId = row.UserId,
                Role = row.Role,
                CreatedByUserId = row.CreatedByUserId,
                CreatedAtUtc = context.Now,
            };
        }),
        out _,
        teamMembershipRows.Length
    )
    .BuildAsync();

Console.WriteLine(
    "0factor E2E fixture upserted: four local identities, two organizations, four teams, six active organization memberships, nine team memberships, and Dana's pending Alpha invitation."
);

static bool IsLoopbackHost(string? host)
{
    return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
}

sealed record FixtureSeed;

sealed record PersonaFixture(string UserId, string Subject, string DisplayName, string Email);

sealed record OrganizationFixture(
    string Id,
    string DisplayName,
    string Slug,
    string CreatedByUserId
);

sealed record TeamFixture(
    string Id,
    string OrganizationId,
    string DisplayName,
    bool IsRootTeam,
    string CreatedByUserId
);

sealed record OrganizationMembershipFixture(
    string Id,
    string OrganizationId,
    string? UserId,
    string Role,
    bool IsDefault,
    string CreatedByUserId,
    string? InvitedEmail = null,
    MembershipStatus Status = MembershipStatus.Active
);

sealed record TeamMembershipFixture(
    string OrganizationId,
    string TeamId,
    string UserId,
    string Role,
    string CreatedByUserId
);

/// <summary>Upserts only the stable 0factor rows and rolls back on an ownership mismatch.</summary>
sealed class FixtureStore(PostgresDbContext db) : IBiomeStore
{
    private const string DanaInvitationId = "inv_0factor_e2e_dana_alpha";
    private const string DanaInvitationEmail = "dana@e2e.example.test";

    public async ValueTask SaveAsync(
        IReadOnlyList<object> entities,
        CancellationToken cancellationToken
    )
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        foreach (var candidate in entities)
        {
            var candidateEntry = db.Entry(candidate);
            var primaryKey =
                candidateEntry.Metadata.FindPrimaryKey()
                ?? throw new InvalidOperationException("A fixture entity has no primary key.");
            var key = primaryKey
                .Properties.Select(property => candidateEntry.Property(property.Name).CurrentValue)
                .ToArray();
            var existing = await db.FindAsync(candidate.GetType(), key, cancellationToken);

            if (existing is null)
            {
                db.Add(candidate);
                continue;
            }

            await EnsureOwnedAsync(existing, candidate, cancellationToken);
            UpdateFixtureFields(existing, candidate);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async ValueTask EnsureOwnedAsync(
        object existing,
        object candidate,
        CancellationToken cancellationToken
    )
    {
        if (
            candidate is OrganizationMembership { Id: DanaInvitationId } invitationCandidate
            && existing is OrganizationMembership invitation
        )
        {
            var belongsToDana =
                invitation.UserId is null
                || await db.ExternalUserIdentities.AnyAsync(
                    identity =>
                        identity.Provider == "mock"
                        && identity.ProviderSubject == "e2e-dana-invitee"
                        && identity.UserId == invitation.UserId,
                    cancellationToken
                );
            EnsureOwned(
                invitation.OrganizationId == invitationCandidate.OrganizationId
                    && invitation.CreatedByUserId == invitationCandidate.CreatedByUserId
                    && invitation.InvitedEmail == DanaInvitationEmail
                    && belongsToDana
            );
            return;
        }

        var belongsToFixture = (existing, candidate) switch
        {
            (User stored, User fixture) => stored.Email == fixture.Email,
            (ExternalUserIdentity stored, ExternalUserIdentity fixture) => stored.UserId
                == fixture.UserId,
            (Organization stored, Organization fixture) => stored.CreatedByUserId
                == fixture.CreatedByUserId,
            (Team stored, Team fixture) => stored.OrganizationId == fixture.OrganizationId
                && stored.CreatedByUserId == fixture.CreatedByUserId
                && stored.IsRootTeam == fixture.IsRootTeam,
            (OrganizationMembership stored, OrganizationMembership fixture) => stored.OrganizationId
                == fixture.OrganizationId
                && stored.UserId == fixture.UserId
                && stored.CreatedByUserId == fixture.CreatedByUserId,
            (TeamMembership stored, TeamMembership fixture) => stored.CreatedByUserId
                == fixture.CreatedByUserId,
            _ => false,
        };

        EnsureOwned(belongsToFixture);
    }

    private void UpdateFixtureFields(object existing, object candidate)
    {
        switch (existing, candidate)
        {
            case (User stored, User fixture):
                stored.DisplayName = fixture.DisplayName;
                stored.Email = fixture.Email;
                stored.EmailVerified = fixture.EmailVerified;
                stored.UpdatedAtUtc = fixture.UpdatedAtUtc;
                stored.DisabledAtUtc = null;
                break;
            case (ExternalUserIdentity stored, ExternalUserIdentity fixture):
                stored.Email = fixture.Email;
                stored.EmailVerified = fixture.EmailVerified;
                stored.DisplayName = fixture.DisplayName;
                stored.LastSeenAtUtc = fixture.LastSeenAtUtc;
                stored.DisabledAtUtc = null;
                break;
            case (Organization stored, Organization fixture):
                stored.DisplayName = fixture.DisplayName;
                stored.Slug = fixture.Slug;
                stored.UpdatedAtUtc = fixture.UpdatedAtUtc;
                stored.ActivatedAtUtc = fixture.ActivatedAtUtc;
                stored.DisabledAtUtc = null;
                break;
            case (Team stored, Team fixture):
                stored.DisplayName = fixture.DisplayName;
                stored.UpdatedAtUtc = fixture.UpdatedAtUtc;
                stored.DisabledAtUtc = null;
                break;
            case (OrganizationMembership stored, OrganizationMembership fixture):
                stored.Role = fixture.Role;
                stored.Status = fixture.Status;
                stored.IsSameDomainAutoInvite = fixture.IsSameDomainAutoInvite;
                stored.IsDefault = fixture.IsDefault;
                stored.DisabledAtUtc = null;
                var entry = db.Entry(stored);
                entry.Property(row => row.UserId).CurrentValue = fixture.UserId;
                entry.Property(row => row.InvitedEmail).CurrentValue = fixture.InvitedEmail;
                entry.Property(row => row.ExpiresAtUtc).CurrentValue = fixture.ExpiresAtUtc;
                break;
            case (TeamMembership stored, TeamMembership fixture):
                stored.Role = fixture.Role;
                stored.DisabledAtUtc = null;
                break;
            default:
                throw new InvalidOperationException(
                    "The fixture contains an unsupported Biome entity."
                );
        }
    }

    private static void EnsureOwned(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException(
                "A stable 0factor fixture row has unexpected ownership; the seed transaction was rolled back."
            );
        }
    }
}
