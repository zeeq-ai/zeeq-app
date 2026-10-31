import type { AuditHistoryEntryType } from "./audit-history-types";

export type AuditActorDescriptor = {
  kind: string;
  displayName: string;
  channel: string;
  credentialName?: string | null;
};

/** Converts the stable product event names into readable, short action text. */
export function formatAuditAction(
  eventType: string,
  changeType: AuditHistoryEntryType,
): string {
  const actions: Record<string, string> = {
    "document.created": "added",
    "document.updated": "edited",
    "document.deleted": "deleted",
    "document.renamed": "renamed",
    "document.review_exclusion_changed": "changed code review inclusion for",
    "document.skill_scope_changed": "changed skill scope for",
    "document.synced": "synced",
    "organization.admin_state_changed": "changed organization settings for",
    "organization.activated": "activated organization",
    "membership.role_changed": "changed the role of",
    "membership.removed": "removed",
    "membership.left": "left",
    "activation_key.created": "created activation key",
    "activation_key.revoked": "revoked activation key",
    "activation_key.redeemed": "redeemed activation key",
    "library.created": "created library",
    "library.updated": "updated library",
    "library.deleted": "deleted library",
    "repository.library_mapping_changed": "changed library mapping for",
  };

  return actions[eventType] ?? fallbackAction(changeType);
}

/** Keeps credential identity separate from the human who owns or initiated it. */
export function formatAuditActor(actor: AuditActorDescriptor): {
  label: string;
  channelLabel: string;
  isSystemAdmin: boolean;
} {
  const credentialName = actor.credentialName?.trim();
  const label =
    actor.kind === "client_credential"
      ? credentialName || actor.displayName || "API credential"
      : actor.kind === "user_token"
        ? actor.displayName || "API token"
        : actor.displayName || "Unknown actor";

  return {
    label,
    channelLabel: formatAuditChannel(actor.channel, actor.kind),
    isSystemAdmin: actor.kind === "system_admin",
  };
}

/** Uses only the documented actor channel values. */
export function formatAuditChannel(
  channel: string,
  actorKind?: string,
): string {
  if (channel === "api" && actorKind === "client_credential") {
    return "via API credential";
  }

  if (channel === "api" && actorKind === "user_token") {
    return "via API token";
  }

  const labels: Record<string, string> = {
    web: "via Web",
    api: "via API",
    mcp: "via MCP",
    worker: "via Worker",
    system_console: "via System console",
    "webhook:github": "via GitHub webhook",
    "webhook:notion": "via Notion webhook",
    deployment: "via Deployment",
  };

  return labels[channel] ?? "via other channel";
}

export function formatAuditArea(area: string): string {
  switch (area) {
    case "ReviewConfiguration":
      return "Review config";
    case "Content":
      return "Content";
    case "Settings":
      return "Settings";
    case "Security":
      return "Security";
    default:
      return "Other";
  }
}

export function isAuditPartialOutcome(outcome: string | null | undefined) {
  return outcome === "partial" || outcome === "incomplete";
}

/** Keeps unverified source references as plain text. */
export function formatAuditSourceReference(
  sourceReference: string | null | undefined,
  subjectType: string,
  scope: "Organization" | "System",
): string {
  if (sourceReference?.trim()) {
    if (sourceReference.startsWith("notion://")) return "Notion";
    try {
      const url = new URL(sourceReference);
      if (
        url.protocol === "https:" &&
        (url.hostname === "github.com" || url.hostname === "www.github.com")
      ) {
        return "GitHub";
      }
    } catch {
      // Unverified references remain plain text.
    }
    return sourceReference.trim();
  }

  if (scope === "System") {
    return "Platform";
  }

  switch (subjectType.toLowerCase()) {
    case "document":
      return "Library";
    case "organization":
      return "Organization";
    case "membership":
      return "Membership";
    case "activationkey":
      return "Activation key";
    case "library":
      return "Library";
    case "repository":
      return "Repository";
    default:
      return "Organization change";
  }
}

function fallbackAction(type: AuditHistoryEntryType): string {
  switch (type) {
    case "added":
      return "added";
    case "deleted":
      return "deleted";
    default:
      return "changed";
  }
}
