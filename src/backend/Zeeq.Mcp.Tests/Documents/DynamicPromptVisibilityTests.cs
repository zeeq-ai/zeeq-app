using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using NSubstitute;
using OpenIddict.Abstractions;
using Zeeq.Core.Documents;
using Zeeq.Core.Identity;

namespace Zeeq.Mcp.Documents.Tests;

public sealed class DynamicPromptVisibilityTests
{
    [Test]
    public async Task RestrictedSkillIsHiddenFromDiscoveryAndRetrieval()
    {
        var documents = Substitute.For<ILibraryDocumentStore>();
        var visibility = Substitute.For<ILibraryVisibilityStore>();
        var document = new LibraryScopedSkillDocument(
            "org",
            "lib",
            "handbook",
            "doc",
            "/checks.md",
            "Checks",
            "checks",
            null,
            null,
            null,
            null,
            "Restricted guidance.",
            DateTimeOffset.UtcNow
        );
        documents
            .ListScopedSkillDocumentsAsync("org", LibraryDocumentScopedSkill.Organization, default)
            .Returns(Task.FromResult<IReadOnlyList<LibraryScopedSkillDocument>>([document]));
        documents
            .GetScopedSkillDocumentAsync(
                "org",
                "lib",
                "doc",
                LibraryDocumentScopedSkill.Organization,
                default
            )
            .Returns(document);
        var memberAudience = new LibraryAudience("org", "member", false);
        visibility
            .ListVisibleAsync(memberAudience, default)
            .Returns(Task.FromResult<IReadOnlyList<Library>>([]));
        var http = new HttpContextAccessor();
        var renderer = new RepositoryPromptRenderer(
            null!,
            null!,
            http,
            null!,
            NullLogger<RepositoryPromptRenderer>.Instance
        );
        var service = new DynamicPromptsService(
            documents,
            visibility,
            renderer,
            http,
            NullLogger<DynamicPromptsService>.Instance
        );
        var request = new GetPromptRequestParams { Name = "checks" };

        var allowed = await service.GetPromptAsync(User("admin", "admin"), request, default);
        await Assert
            .That(((TextContentBlock)allowed.Messages.Single().Content).Text)
            .IsEqualTo(document.Content);
        var list = await service.ListPromptsAsync(User("member", "member"), default);
        await Assert.That(list.Prompts).IsEmpty();
        await Assert
            .That(async () =>
                await service.GetPromptAsync(User("member", "member"), request, default)
            )
            .Throws<McpProtocolException>();
        await documents
            .Received(1)
            .GetScopedSkillDocumentAsync(
                "org",
                "lib",
                "doc",
                LibraryDocumentScopedSkill.Organization,
                default
            );
    }

    private static ClaimsPrincipal User(string userId, string role) =>
        new(
            new ClaimsIdentity(
                [
                    new(OpenIddictConstants.Claims.Subject, userId),
                    new(AuthClaims.OrganizationId, "org"),
                    new(ClaimTypes.Role, role),
                ],
                authenticationType: "test"
            )
        );
}
