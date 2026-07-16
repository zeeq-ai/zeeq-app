using System.Security.Claims;
using System.Security.Cryptography;
using Zeeq.Core.Common.AspNetCore.Contracts;
using Zeeq.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;

namespace Zeeq.Core.Identity;

/// <summary>
/// Creates a user-owned confidential OAuth client for non-interactive MCP access.
/// </summary>
/// <remarks>
/// Creation writes both an OpenIddict application and a local
/// <see cref="ClientCredential"/> metadata row. OpenIddict owns client-secret
/// validation; the local row owns list/revoke UI, owner binding, tenant context,
/// and audit metadata from the browser login that created the credential.
/// </remarks>
public sealed partial class CreateClientCredentialHandler(
    IOpenIddictApplicationManager manager,
    IZeeqIdentityStore identityStore,
    AuthSettings settings,
    ILogger<CreateClientCredentialHandler> logger
) : IEndpointHandler
{
    /// <summary>
    /// Validates the display name, registers the confidential client, and returns the secret once.
    /// </summary>
    /// <remarks>
    /// The plaintext secret is intentionally present only in the creation response.
    /// List endpoints must use <see cref="ClientCredentialSummary"/> and never return
    /// the stored secret value.
    /// </remarks>
    public async Task<Results<Created<ClientCredentialCreated>, ValidationProblem>> HandleAsync(
        ClientCredentialCreateRequest request,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var displayName = request.DisplayName?.Trim();
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return TypedResults.ValidationProblem(
                new Dictionary<string, string[]>
                {
                    [nameof(request.DisplayName)] = ["Display name is required."],
                }
            );
        }

        var owner = AuthenticatedOwnerContext.FromClaimsPrincipal(user);
        var clientId = "auth_cred_" + Guid.NewGuid().ToString("N");
        var clientSecret = "auth_secret_" + CreateSecretValue();
        var createdAt = DateTimeOffset.UtcNow;

        var descriptor = ClientCredentialOpenIddictFactory.CreateApplicationDescriptor(
            clientId,
            clientSecret,
            displayName,
            settings
        );
        await manager.CreateAsync(descriptor, cancellationToken);

        await identityStore.AddClientCredentialAsync(
            new ClientCredential
            {
                ClientId = clientId,
                ClientSecret = clientSecret,
                CreatedAtUtc = createdAt,
                DisplayName = displayName,
                OrganizationId = owner.OrganizationId,
                OwnerProvider = owner.Provider,
                OwnerProviderSubject = owner.ProviderSubject,
                OwnerUserId = owner.UserId,
                SelectedPartitionIdsJson = owner.PartitionIdsJson,
                TeamId = owner.TeamId,
            },
            cancellationToken
        );

        LogCreatedCredential(logger, clientId, owner.UserId, displayName);

        var response = ClientCredentialCreated.Create(
            clientId,
            clientSecret,
            displayName,
            createdAt,
            settings
        );

        return TypedResults.Created(
            $"/api/v1/orgs/{Uri.EscapeDataString(owner.OrganizationId)}/clients/{Uri.EscapeDataString(clientId)}",
            response
        );
    }

    private static string CreateSecretValue()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlEncoder.Encode(bytes);
    }

    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Information,
        Message = "Created local client credential. ClientId={ClientId}, OwnerUserId={OwnerUserId}, DisplayName={DisplayName}"
    )]
    private static partial void LogCreatedCredential(
        ILogger logger,
        string clientId,
        string ownerUserId,
        string displayName
    );
}
