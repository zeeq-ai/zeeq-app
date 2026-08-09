using System.Text.Json;

namespace Zeeq.Integrations.Notion.Tests;

public sealed class NotionWebhookContractsTests
{
    [Test]
    public async Task VerificationChallenge_UsesNotionPropertyName()
    {
        var challenge = JsonSerializer.Deserialize<NotionWebhookVerificationChallenge>(
            """{"verification_token":"verify-me"}"""
        );

        await Assert.That(challenge!.VerificationToken).IsEqualTo("verify-me");
    }

    [Test]
    public async Task SignedEvent_DeserializesRoutingAndActivationFields()
    {
        var webhookEvent = JsonSerializer.Deserialize<NotionWebhookEvent>(
            """
            {
              "id": "event-1",
              "type": "page.undeleted",
              "timestamp": "2026-08-08T12:30:00Z",
              "subscription_id": "subscription-1",
              "workspace_id": "workspace-1",
              "integration_id": "integration-1",
              "attempt_number": 2,
              "entity": { "id": "page-1", "type": "page" }
            }
            """
        );

        await Assert.That(webhookEvent!.Id).IsEqualTo("event-1");
        await Assert.That(webhookEvent.Type).IsEqualTo("page.undeleted");
        await Assert.That(webhookEvent.SubscriptionId).IsEqualTo("subscription-1");
        await Assert.That(webhookEvent.WorkspaceId).IsEqualTo("workspace-1");
        await Assert.That(webhookEvent.AttemptNumber).IsEqualTo(2);
        await Assert.That(webhookEvent.Entity!.Id).IsEqualTo("page-1");
        await Assert.That(webhookEvent.Entity.Type).IsEqualTo("page");
    }
}
