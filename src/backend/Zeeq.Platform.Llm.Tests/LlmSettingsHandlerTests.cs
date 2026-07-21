using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Caching.Memory;
using OpenIddict.Abstractions;
using Zeeq.Core.Common;
using Zeeq.Core.Identity;
using Zeeq.Core.Llm;
using Zeeq.Core.Models;
using Zeeq.Platform.Llm;

namespace Zeeq.Platform.Llm.Tests;

/// <summary>
/// Handler tests for organization-scoped LLM settings APIs.
///
/// Run:
/// dotnet run --project src/backend/Zeeq.Platform.Llm.Tests --output detailed --disable-logo
/// </summary>
public sealed class LlmSettingsHandlerTests
{
    private const string OrganizationId = "org_test";
    private const string UserId = "usr_test";

    [Test]
    public async Task GetLlmSettingsHandler_WithNonAdminMember_ReturnsAccessNoticeWithoutLoadingSettings()
    {
        var fixture = Fixture.Create(role: "member");
        var handler = new GetLlmSettingsHandler(
            fixture.Authorization,
            fixture.Settings,
            fixture.EncryptedValues
        );

        var result = await handler.HandleAsync(OrganizationId, User(), CancellationToken.None);
        var response = ((Ok<LlmSettingsViewResponse>)result).Value!;

        await Assert.That(response.CanManage).IsFalse();
        await Assert.That(response.Notice).IsEqualTo("This view requires admin or owner access.");
        await Assert.That(response.Configuration).IsNull();
        await Assert.That(response.Keys).IsEmpty();
        await Assert.That(fixture.Settings.FindCount).IsEqualTo(0);
        await Assert.That(fixture.EncryptedValues.ListCount).IsEqualTo(0);
    }

    [Test]
    public async Task SaveLlmSettingsHandler_WithOpenAiDefaultKey_ReturnsBadRequest()
    {
        var fixture = Fixture.Create(role: "admin");
        var handler = new SaveLlmSettingsHandler(
            fixture.Authorization,
            fixture.Settings,
            fixture.EncryptedValues
        );
        var request = new SaveLlmSettingsRequest(
            new LlmTierSettingsRequest("OpenAI", "gpt-5.4-mini", null, null),
            new LlmTierSettingsRequest(
                "Fireworks",
                "accounts/fireworks/models/glm-5p2",
                null,
                null
            ),
            new LlmTierSettingsRequest("Fireworks", "accounts/fireworks/models/glm-5p2", null, null)
        );

        var result = await handler.HandleAsync(
            OrganizationId,
            request,
            User(),
            CancellationToken.None
        );
        var response = ((BadRequest<LlmSettingsError>)result).Value!;

        await Assert
            .That(response.Message)
            .IsEqualTo("Fast requires a managed API key for OpenAI.");
        await Assert.That(fixture.Settings.UpdateCount).IsEqualTo(0);
    }

    [Test]
    public async Task SaveLlmSettingsHandler_WithMissingReferencedKey_ReturnsBadRequest()
    {
        var fixture = Fixture.Create(role: "admin");
        var handler = new SaveLlmSettingsHandler(
            fixture.Authorization,
            fixture.Settings,
            fixture.EncryptedValues
        );
        var request = new SaveLlmSettingsRequest(
            new LlmTierSettingsRequest("OpenAI", "gpt-5.4-mini", "enc_missing", null),
            new LlmTierSettingsRequest(
                "Fireworks",
                "accounts/fireworks/models/glm-5p2",
                null,
                null
            ),
            new LlmTierSettingsRequest("Fireworks", "accounts/fireworks/models/glm-5p2", null, null)
        );

        var result = await handler.HandleAsync(
            OrganizationId,
            request,
            User(),
            CancellationToken.None
        );
        var response = ((BadRequest<LlmSettingsError>)result).Value!;

        await Assert
            .That(response.Message)
            .IsEqualTo("Referenced key 'enc_missing' was not found.");
        await Assert.That(fixture.Settings.UpdateCount).IsEqualTo(0);
    }

    [Test]
    public async Task SaveLlmSettingsHandler_WithFireworksAndNoKey_Succeeds()
    {
        var fixture = Fixture.Create(role: "admin");
        var handler = new SaveLlmSettingsHandler(
            fixture.Authorization,
            fixture.Settings,
            fixture.EncryptedValues
        );
        var request = new SaveLlmSettingsRequest(
            new LlmTierSettingsRequest(
                "Fireworks",
                "accounts/fireworks/models/glm-5p2",
                null,
                null
            ),
            new LlmTierSettingsRequest(
                "Fireworks",
                "accounts/fireworks/models/glm-5p2",
                null,
                null
            ),
            new LlmTierSettingsRequest("Fireworks", "accounts/fireworks/models/glm-5p2", null, null)
        );

        var result = await handler.HandleAsync(
            OrganizationId,
            request,
            User(),
            CancellationToken.None
        );

        await Assert.That(result).IsTypeOf<Ok<LlmSettingsViewResponse>>();
        await Assert.That(fixture.Settings.UpdateCount).IsEqualTo(1);
    }

    [Test]
    public async Task CreateLlmApiKeyHandler_WithPlaintextKey_ReturnsMetadataOnly()
    {
        var fixture = Fixture.Create(role: "owner");
        var handler = new CreateLlmApiKeyHandler(fixture.Authorization, fixture.KeyEncryption);

        var result = await handler.HandleAsync(
            OrganizationId,
            new CreateLlmApiKeyRequest("OpenAI production", "sk-secret-value"),
            User(),
            CancellationToken.None
        );
        var response = ((Created<LlmApiKeyResponse>)result).Value!;

        await Assert.That(response.Id).StartsWith("enc_");
        await Assert.That(response.Name).IsEqualTo("OpenAI production");
        await Assert.That(response.ToString()).DoesNotContain("sk-secret-value");
        await Assert
            .That(Convert.ToBase64String(fixture.EncryptedValues.Values.Single().Ciphertext))
            .DoesNotContain("sk-secret-value");
    }

    [Test]
    public async Task DeleteLlmApiKeyHandler_WithReferencedKey_ReturnsBadRequest()
    {
        var fixture = Fixture.Create(role: "admin");
        var key = fixture.EncryptedValues.AddExisting("enc_used");
        fixture.Settings.Configuration = fixture.Settings.Configuration with
        {
            Fast = fixture.Settings.Configuration.Fast with { KeyId = key.Id },
        };
        var handler = new DeleteLlmApiKeyHandler(
            fixture.Authorization,
            fixture.Settings,
            fixture.EncryptedValues
        );

        var result = await handler.HandleAsync(
            OrganizationId,
            key.Id,
            User(),
            CancellationToken.None
        );
        var response = ((BadRequest<LlmSettingsError>)result).Value!;

        await Assert
            .That(response.Message)
            .IsEqualTo("Key cannot be deleted while it is referenced by LLM settings.");
        await Assert.That(key.DisabledAtUtc).IsNull();
    }

    [Test]
    public async Task TestLlmSettingsHandler_WithOpenAiDefaultKey_ReturnsBadRequest()
    {
        var fixture = Fixture.Create(role: "owner");
        var handler = new TestLlmSettingsHandler(
            fixture.Authorization,
            fixture.AppSettings,
            fixture.KeyEncryption,
            fixture.ProviderTester
        );

        var result = await handler.HandleAsync(
            OrganizationId,
            new TestLlmSettingsRequest(
                Tier: "fast",
                Provider: "OpenAI",
                Model: "gpt-5.4-mini",
                KeyId: null,
                Prompt: "Reply with OK.",
                Endpoint: null
            ),
            User(),
            CancellationToken.None
        );
        var response = ((BadRequest<LlmSettingsError>)result).Value!;

        await Assert.That(response.Message).IsEqualTo("A managed API key is required for OpenAI.");
        await Assert.That(fixture.ProviderTester.LastConfiguration).IsNull();
    }

    [Test]
    public async Task TestLlmSettingsHandler_WithDefaultFireworksFastKey_UsesDefaultApiKey()
    {
        var fixture = Fixture.Create(role: "owner");
        var handler = new TestLlmSettingsHandler(
            fixture.Authorization,
            fixture.AppSettings,
            fixture.KeyEncryption,
            fixture.ProviderTester
        );

        var result = await handler.HandleAsync(
            OrganizationId,
            new TestLlmSettingsRequest(
                Tier: "fast",
                Provider: "Fireworks",
                Model: "accounts/fireworks/models/glm-5p2",
                KeyId: null,
                Prompt: "Reply with OK.",
                Endpoint: null
            ),
            User(),
            CancellationToken.None
        );
        var response = ((Ok<LlmProviderAccessTestResult>)result).Value!;

        await Assert.That(response.Success).IsTrue();
        await Assert
            .That(fixture.ProviderTester.LastConfiguration?.ApiKey)
            .IsEqualTo("default-fast-key");
        await Assert
            .That(fixture.ProviderTester.LastConfiguration?.Provider)
            .IsEqualTo("Fireworks");
    }

    [Test]
    public async Task TestLlmSettingsHandler_WithDefaultFireworksMaxKey_UsesConfiguredEndpoint()
    {
        var fixture = Fixture.Create(role: "owner");
        var handler = new TestLlmSettingsHandler(
            fixture.Authorization,
            fixture.AppSettings,
            fixture.KeyEncryption,
            fixture.ProviderTester
        );

        var result = await handler.HandleAsync(
            OrganizationId,
            new TestLlmSettingsRequest(
                Tier: "max",
                Provider: "Fireworks",
                Model: "accounts/fireworks/models/glm-5p2",
                KeyId: null,
                Prompt: "Reply with OK.",
                Endpoint: null
            ),
            User(),
            CancellationToken.None
        );
        var response = ((Ok<LlmProviderAccessTestResult>)result).Value!;

        await Assert.That(response.Success).IsTrue();
        await Assert
            .That(fixture.ProviderTester.LastConfiguration?.Endpoint)
            .IsEqualTo("https://api.fireworks.ai/inference/v1");
        await Assert.That(fixture.ProviderTester.LastConfiguration?.KeySource).IsEqualTo("default");
    }

    private static ClaimsPrincipal User() =>
        new(
            new ClaimsIdentity(
                [new Claim(OpenIddictConstants.Claims.Subject, UserId)],
                authenticationType: "test"
            )
        );

    private sealed class Fixture
    {
        public required FakeMembershipStore Memberships { get; init; }

        public required LlmSettingsAuthorization Authorization { get; init; }

        public required FakeLlmSettingsStore Settings { get; init; }

        public required FakeEncryptedValueStore EncryptedValues { get; init; }

        public required KeyEncryptionService KeyEncryption { get; init; }

        public required FakeLlmProviderAccessTester ProviderTester { get; init; }

        public required LlmSettings AppSettings { get; init; }

        public static Fixture Create(string role)
        {
            var memberships = new FakeMembershipStore(role);
            var settings = new FakeLlmSettingsStore();
            var encryptedValues = new FakeEncryptedValueStore();
            var appSettings = new LlmSettings
            {
                Models = new LlmModelDefaults
                {
                    Fast = new LlmModelDefault
                    {
                        Provider = "OpenAI",
                        Model = "gpt-5.4-mini",
                        ApiKey = "default-fast-key",
                    },
                    Max = new LlmModelDefault
                    {
                        Provider = "Fireworks",
                        Model = "accounts/fireworks/models/glm-5p2",
                        Endpoint = "https://api.fireworks.ai/inference/v1",
                    },
                },
                EncryptionProvider = "test-provider",
            };

            return new Fixture
            {
                Memberships = memberships,
                Authorization = new LlmSettingsAuthorization(memberships),
                Settings = settings,
                EncryptedValues = encryptedValues,
                KeyEncryption = new KeyEncryptionService(
                    appSettings,
                    encryptedValues,
                    [new FakeDataEncryptionProvider()],
                    new MemoryCache(new MemoryCacheOptions())
                ),
                ProviderTester = new FakeLlmProviderAccessTester(),
                AppSettings = appSettings,
            };
        }
    }

    private sealed class FakeMembershipStore(string role) : IZeeqMembershipStore
    {
        public Task<IReadOnlyList<OrganizationMembership>> ListActiveMembershipsForUserAsync(
            string userId,
            CancellationToken ct
        ) =>
            Task.FromResult<IReadOnlyList<OrganizationMembership>>([
                new()
                {
                    Id = "mem_test",
                    OrganizationId = OrganizationId,
                    UserId = userId,
                    Role = role,
                    Status = MembershipStatus.Active,
                    CreatedByUserId = userId,
                },
            ]);

        public Task<Organization?> FindOrganizationByIdAsync(string orgId, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<OrganizationActivationState?> FindOrganizationActivationStateAsync(
            string orgId,
            CancellationToken ct
        ) =>
            Task.FromResult<OrganizationActivationState?>(
                new(
                    OrganizationId: orgId,
                    ActivatedAtUtc: DateTimeOffset.UtcNow,
                    DisabledAtUtc: null
                )
            );

        public Task<IReadOnlyList<Organization>> FindOrganizationsByIdsAsync(
            string[] orgIds,
            CancellationToken ct
        ) => throw new NotImplementedException();

        public Task<Organization?> FindOrganizationBySlugAsync(string slug, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<bool> IsSlugAvailableAsync(
            string slug,
            string? excludeOrgId,
            CancellationToken ct
        ) => throw new NotImplementedException();

        public Task UpdateOrganizationAsync(Organization org, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<bool> UpdateOrganizationSameDomainOnboardingAsync(
            Organization organization,
            CancellationToken ct
        ) => throw new NotImplementedException();

        public Task<string?> FindUserEmailByIdAsync(string userId, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<IReadOnlyDictionary<string, string?>> FindUserEmailsByIdsAsync(
            string[] userIds,
            CancellationToken ct
        ) => throw new NotImplementedException();

        public Task<bool> IsAutoInviteSameDomainAvailableAsync(
            string domain,
            string excludeOrgId,
            CancellationToken ct
        ) => throw new NotImplementedException();

        public Task<IReadOnlyDictionary<string, string>> FindAutoInviteSameDomainClaimsAsync(
            string[] domains,
            CancellationToken ct
        ) => throw new NotImplementedException();

        public Task<int> CountOrganizationsCreatedByUserAsync(
            string userId,
            CancellationToken ct
        ) => throw new NotImplementedException();

        public Task<Organization?> CreateOrganizationAsync(
            Organization organization,
            Team rootTeam,
            OrganizationMembership ownerMembership,
            TeamMembership rootTeamMembership,
            int maxCreatedOrganizations,
            CancellationToken ct
        ) => throw new NotImplementedException();

        public Task<IReadOnlyList<OrganizationMember>> ListMembersForOrganizationAsync(
            string orgId,
            CancellationToken ct
        ) => throw new NotImplementedException();

        public Task SetDefaultOrganizationAsync(
            string userId,
            string orgId,
            CancellationToken ct
        ) => throw new NotImplementedException();

        public Task UpdateMemberRoleAsync(
            string orgId,
            string userId,
            string newRole,
            CancellationToken ct
        ) => throw new NotImplementedException();

        public Task RemoveMemberAsync(string orgId, string userId, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task LeaveOrganizationAsync(string orgId, string userId, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<string?> FindRootTeamIdForMemberAsync(
            string orgId,
            string userId,
            CancellationToken ct
        ) => throw new NotImplementedException();

        public Task<OrganizationMembership> CreateInvitationAsync(
            OrganizationMembership invitation,
            CancellationToken ct
        ) => throw new NotImplementedException();

        public Task<IReadOnlyList<OrganizationMembership>> ListPendingInvitationsForEmailAsync(
            string email,
            CancellationToken ct
        ) => throw new NotImplementedException();

        public Task<
            IReadOnlyList<OrganizationMembership>
        > ListPendingInvitationsForOrganizationAsync(string orgId, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<bool> AcceptInvitationAsync(
            string membershipId,
            string userId,
            CancellationToken ct
        ) => throw new NotImplementedException();

        public Task<bool> AcceptInvitationAsDefaultAsync(
            string membershipId,
            string userId,
            CancellationToken ct
        ) => throw new NotImplementedException();

        public Task<SameDomainInvitationDetails?> FindSameDomainInvitationDetailsAsync(
            string membershipId,
            string email,
            CancellationToken ct
        ) => throw new NotImplementedException();

        public Task<bool> DeclineInvitationAsync(string membershipId, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<bool> CancelInvitationAsync(
            string orgId,
            string membershipId,
            CancellationToken ct
        ) => throw new NotImplementedException();
    }

    private sealed class FakeLlmSettingsStore : ILlmSettingsStore
    {
        public int FindCount { get; private set; }

        public int UpdateCount { get; private set; }

        public OrganizationLlmConfiguration Configuration { get; set; } =
            OrganizationLlmConfiguration.Default;

        public Task<OrganizationLlmConfiguration?> FindConfigurationAsync(
            string organizationId,
            CancellationToken cancellationToken
        )
        {
            FindCount++;
            return Task.FromResult<OrganizationLlmConfiguration?>(Configuration);
        }

        public Task<bool> UpdateConfigurationAsync(
            string organizationId,
            OrganizationLlmConfiguration configuration,
            DateTimeOffset updatedAtUtc,
            CancellationToken cancellationToken
        )
        {
            UpdateCount++;
            Configuration = configuration;
            return Task.FromResult(true);
        }
    }

    private sealed class FakeEncryptedValueStore : IEncryptedValueStore
    {
        private readonly List<EncryptedValue> _values = [];

        public IReadOnlyList<EncryptedValue> Values => _values;

        public int ListCount { get; private set; }

        public EncryptedValue AddExisting(string id)
        {
            var value = new EncryptedValue
            {
                Id = id,
                OrganizationId = OrganizationId,
                Kind = EncryptedValueKind.LlmApiKey,
                EncryptionProvider = "test-provider",
                Name = "Test key",
                Ciphertext = [1, 2, 3],
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };

            _values.Add(value);
            return value;
        }

        public Task<IReadOnlyList<EncryptedValue>> ListActiveAsync(
            string organizationId,
            EncryptedValueKind kind,
            CancellationToken cancellationToken
        )
        {
            ListCount++;
            return Task.FromResult<IReadOnlyList<EncryptedValue>>(
                _values
                    .Where(value =>
                        value.OrganizationId == organizationId
                        && value.Kind == kind
                        && value.DisabledAtUtc is null
                    )
                    .ToArray()
            );
        }

        public Task<EncryptedValue?> FindActiveAsync(
            string organizationId,
            string id,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                _values.FirstOrDefault(value =>
                    value.OrganizationId == organizationId
                    && value.Id == id
                    && value.DisabledAtUtc is null
                )
            );

        public Task<EncryptedValue> AddAsync(
            EncryptedValue value,
            CancellationToken cancellationToken
        )
        {
            _values.Add(value);
            return Task.FromResult(value);
        }

        public Task<bool> UpdateAsync(EncryptedValue value, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<bool> DisableAsync(
            string organizationId,
            string id,
            DateTimeOffset disabledAtUtc,
            CancellationToken cancellationToken
        )
        {
            var value = _values.FirstOrDefault(value =>
                value.OrganizationId == organizationId
                && value.Id == id
                && value.DisabledAtUtc is null
            );

            if (value is null)
            {
                return Task.FromResult(false);
            }

            value.DisabledAtUtc = disabledAtUtc;
            value.UpdatedAtUtc = disabledAtUtc;
            return Task.FromResult(true);
        }
    }

    private sealed class FakeDataEncryptionProvider : IDataEncryptionProvider
    {
        public string ProviderName => "test-provider";

        public Task<byte[]> EncryptAsync(
            string organizationId,
            ReadOnlyMemory<byte> plaintext,
            CancellationToken cancellationToken
        ) => Task.FromResult(plaintext.ToArray().Reverse().ToArray());

        public Task<byte[]> DecryptAsync(
            string organizationId,
            ReadOnlyMemory<byte> ciphertext,
            CancellationToken cancellationToken
        ) => Task.FromResult(ciphertext.ToArray().Reverse().ToArray());
    }

    private sealed class FakeLlmProviderAccessTester : ILlmProviderAccessTester
    {
        public ResolvedLlmConfiguration? LastConfiguration { get; private set; }

        public Task<LlmProviderAccessTestResult> TestAsync(
            ResolvedLlmConfiguration configuration,
            string? prompt,
            CancellationToken cancellationToken
        )
        {
            LastConfiguration = configuration;
            return Task.FromResult(
                new LlmProviderAccessTestResult(
                    Success: true,
                    Provider: configuration.Provider,
                    Model: configuration.Model,
                    LatencyMs: 1,
                    ErrorCode: null,
                    Message: "Provider access test completed."
                )
            );
        }
    }
}
