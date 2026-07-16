using Zeeq.Core.Carts;
using Zeeq.Core.Documents;
using Zeeq.Core.Documents.Snippets;
using Zeeq.Core.Models;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Zeeq.Data.Postgres;

/// <summary>
/// EF Core `DbContext` implementation for Postgres which provides Pg specific configuration.
/// </summary>
public class PostgresDbContext(DbContextOptions<PostgresDbContext> options)
    : ZeeqDbContextBase(options),
        IDataProtectionKeyContext
{
    /// <summary>
    /// ASP.NET Core Data Protection key ring used to decrypt auth cookies across deploys.
    /// </summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    /// <summary>
    /// OAuth client credentials.
    /// </summary>
    public DbSet<ClientCredential> ClientCredentials => Set<ClientCredential>();

    /// <summary>
    /// Dynamic client registration setup rows.
    /// </summary>
    public DbSet<DcrClientSetup> DcrClientSetups => Set<DcrClientSetup>();

    /// <summary>
    /// User-owned bearer token metadata.
    /// </summary>
    public DbSet<UserToken> UserTokens => Set<UserToken>();

    /// <summary>
    /// Local user records.
    /// </summary>
    public DbSet<User> Users => Set<User>();

    /// <summary>
    /// External identity bindings for local users.
    /// </summary>
    public DbSet<ExternalUserIdentity> ExternalUserIdentities => Set<ExternalUserIdentity>();

    /// <summary>
    /// Organization tenant records.
    /// </summary>
    public DbSet<Organization> Organizations => Set<Organization>();

    /// <summary>
    /// Organization-owned encrypted secret values.
    /// </summary>
    public DbSet<EncryptedValue> EncryptedValues => Set<EncryptedValue>();

    /// <summary>
    /// Organization teams.
    /// </summary>
    public DbSet<Team> Teams => Set<Team>();

    /// <summary>
    /// Organization memberships and invitations.
    /// </summary>
    public DbSet<OrganizationMembership> OrganizationMemberships => Set<OrganizationMembership>();

    /// <summary>
    /// Team memberships.
    /// </summary>
    public DbSet<TeamMembership> TeamMemberships => Set<TeamMembership>();

    /// <summary>
    /// Content partitions.
    /// </summary>
    public DbSet<Partition> Partitions => Set<Partition>();

    /// <summary>
    /// Transient authentication state rows.
    /// </summary>
    public DbSet<AuthTransientState> AuthTransientStates => Set<AuthTransientState>();

    /// <summary>
    /// GitHub App installations linked to Zeeq organizations.
    /// </summary>
    public DbSet<GitHubAppInstallation> GitHubAppInstallations => Set<GitHubAppInstallation>();

    /// <summary>
    /// Repository mappings configured for code review.
    /// </summary>
    public DbSet<CodeRepository> CodeRepositories => Set<CodeRepository>();

    /// <summary>
    /// Pull request records used by code-review streams.
    /// </summary>
    public DbSet<PullRequestRecord> PullRequestRecords => Set<PullRequestRecord>();

    /// <summary>
    /// Non-partitioned lookup rows for pull requests.
    /// </summary>
    public DbSet<PullRequestLookup> PullRequestLookups => Set<PullRequestLookup>();

    /// <summary>
    /// Code review execution records.
    /// </summary>
    public DbSet<CodeReviewRecord> CodeReviewRecords => Set<CodeReviewRecord>();

    /// <summary>
    /// Captured application metric measurements (partitioned wide-event table).
    /// </summary>
    public DbSet<MetricEvent> MetricEvents => Set<MetricEvent>();

    /// <summary>
    /// Transient raw agent-telemetry ingest rows (unlogged, lease-claimed).
    /// </summary>
    public DbSet<TelemetryRawRequest> TelemetryRawRequests => Set<TelemetryRawRequest>();

    /// <summary>
    /// Durable agent telemetry conversation rows.
    /// </summary>
    public DbSet<AgentConversation> AgentConversations => Set<AgentConversation>();

    /// <summary>
    /// Unified agent session event rows (partitioned by <c>occurred_at_utc</c>).
    /// </summary>
    public DbSet<AgentSessionEvent> AgentSessionEvents => Set<AgentSessionEvent>();

    /// <summary>
    /// PR-session link rows (many-to-many, non-partitioned).
    /// </summary>
    public DbSet<AgentPullRequestSessionLink> AgentPullRequestSessionLinks => Set<AgentPullRequestSessionLink>();

    /// <summary>
    /// Organization execution capacity leases for running code reviews.
    /// </summary>
    public DbSet<CodeReviewExecutionLease> CodeReviewExecutionLeases =>
        Set<CodeReviewExecutionLease>();

    /// <summary>
    /// Persisted repository-scoped code reviewer agents.
    /// </summary>
    public DbSet<CodeReviewerAgent> CodeReviewerAgents => Set<CodeReviewerAgent>();

    /// <summary>
    /// Active review guard rows.
    /// </summary>
    public DbSet<ActiveCodeReviewLock> ActiveCodeReviewLocks => Set<ActiveCodeReviewLock>();

    /// <summary>
    /// GitHub webhook delivery idempotency rows.
    /// </summary>
    public DbSet<GitHubWebhookDelivery> GitHubWebhookDeliveries => Set<GitHubWebhookDelivery>();

    /// <summary>
    /// Short-lived GitHub comment writer lease rows.
    /// </summary>
    public DbSet<GitHubCommentLease> GitHubCommentLeases => Set<GitHubCommentLease>();

    /// <summary>
    /// Durable GitHub comment target anchor rows.
    /// </summary>
    public DbSet<GitHubCommentAnchor> GitHubCommentAnchors => Set<GitHubCommentAnchor>();

    /// <summary>
    /// Document libraries owned by organizations and teams.
    /// </summary>
    public DbSet<Library> Libraries => Set<Library>();

    /// <summary>
    /// Markdown documents stored within libraries.
    /// </summary>
    public DbSet<LibraryDocument> LibraryDocuments => Set<LibraryDocument>();

    /// <summary>
    /// Searchable snippets (prose sections and code samples) derived from private library documents.
    /// </summary>
    public DbSet<LibraryDocumentSnippet> LibraryDocumentSnippets => Set<LibraryDocumentSnippet>();

    /// <summary>
    /// Searchable snippets derived from global public documents, shared across subscribing orgs.
    /// </summary>
    public DbSet<PublicDocumentSnippet> PublicDocumentSnippets => Set<PublicDocumentSnippet>();

    /// <summary>
    /// Findings carts for saved code-review finding collections.
    /// </summary>
    public DbSet<Cart> Carts => Set<Cart>();

    /// <summary>
    /// Public repository sources registered for global document ingest.
    /// </summary>
    public DbSet<DocsPublicSource> DocsPublicSources => Set<DocsPublicSource>();

    /// <summary>
    /// Documents ingested from public repositories, shared globally across orgs.
    /// </summary>
    public DbSet<DocsPublicDocument> DocsPublicDocuments => Set<DocsPublicDocument>();

    /// <summary>
    /// Ingest run execution records — partitioned by <c>created_at_utc</c>.
    /// </summary>
    public DbSet<DocsIngestRun> DocsIngestRuns => Set<DocsIngestRun>();

    /// <summary>
    /// Local Postgres-backed object storage rows.
    /// </summary>
    internal DbSet<PostgresStorageObject> StorageObjects => Set<PostgresStorageObject>();

    /// <summary>
    /// Configures the EF Core model with Postgres-specific extensions and settings.
    /// </summary>
    protected override void ConfigureProviderModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("vector");
        modelBuilder.HasPostgresExtension("fuzzystrmatch");
        modelBuilder.HasPostgresExtension("pg_trgm");
        modelBuilder.HasPostgresExtension("unaccent");
        modelBuilder.HasPostgresExtension("pg_partman");
        modelBuilder.HasPostgresExtension("pg_cron");
        modelBuilder.HasPostgresExtension("btree_gin");

        modelBuilder.HasDefaultSchema("zeeq");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PostgresDbContext).Assembly);
    }
}
