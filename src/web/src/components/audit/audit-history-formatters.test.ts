import { describe, expect, it } from "vitest";
import {
  formatAuditAction,
  formatAuditActor,
  formatAuditArea,
  formatAuditSourceReference,
  isAuditPartialOutcome,
} from "./audit-history-formatters";

describe("audit history formatters", () => {
  it("uses semantic labels for documented event types", () => {
    expect(formatAuditAction("document.renamed", "edited")).toBe("renamed");
    expect(formatAuditAction("membership.role_changed", "edited")).toBe(
      "changed the role of",
    );
    expect(formatAuditAction("unknown.event", "deleted")).toBe("deleted");
  });

  it("identifies only an explicit system-console actor as a system admin", () => {
    expect(
      formatAuditActor({
        kind: "system_admin",
        displayName: "Sam",
        channel: "system_console",
      }),
    ).toEqual({
      label: "Sam",
      channelLabel: "via System console",
      isSystemAdmin: true,
    });

    expect(
      formatAuditActor({
        kind: "user",
        displayName: "Sam",
        channel: "web",
      }).isSystemAdmin,
    ).toBe(false);
  });

  it("uses credential names without assigning the responsible human as actor", () => {
    expect(
      formatAuditActor({
        kind: "client_credential",
        displayName: "Build bot",
        credentialName: "deploy-prod",
        channel: "api",
      }),
    ).toEqual({
      label: "deploy-prod",
      channelLabel: "via API credential",
      isSystemAdmin: false,
    });
  });

  it("maps areas and marks only explicit partial outcomes", () => {
    expect(formatAuditArea("ReviewConfiguration")).toBe("Review config");
    expect(isAuditPartialOutcome("partial")).toBe(true);
    expect(isAuditPartialOutcome("succeeded")).toBe(false);
    expect(isAuditPartialOutcome(null)).toBe(false);
  });

  it("keeps source references as text and labels known subjects", () => {
    expect(
      formatAuditSourceReference("run-42", "Document", "Organization"),
    ).toBe("run-42");
    expect(formatAuditSourceReference(null, "document", "Organization")).toBe(
      "Library",
    );
    expect(formatAuditSourceReference(null, "ActivationKey", "System")).toBe(
      "Platform",
    );
    expect(
      formatAuditSourceReference(
        "notion://library/library-1",
        "document",
        "Organization",
      ),
    ).toBe("Notion");
    expect(
      formatAuditSourceReference(
        "https://github.com/zeeq-ai/zeeq-app",
        "document",
        "Organization",
      ),
    ).toBe("GitHub");
  });
});
