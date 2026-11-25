import { afterEach, describe, expect, it, vi } from "vitest";
import { createApp, h, nextTick } from "vue";
import FindingTriageControl from "./FindingTriageControl.vue";
import type {
  CodeReviewFindingDto,
  CodeReviewFindingTriageDto,
} from "@/api/generated";
const triage: CodeReviewFindingTriageDto = {
  id: "t",
  sourceReviewId: "r",
  sourceReviewCreatedAtUtc: new Date(),
  occurrenceKey: "F1",
  semanticId: "test-finding-one",
  facet: "security",
  severity: "Major",
  filePath: null,
  lineNumber: null,
  findingSummary: "summary",
  disposition: "FalsePositive",
  approvalState: "Proposed",
  currentReason: "reason",
  channel: "Mcp",
  sourceUrl: null,
  proposedByUserId: null,
  proposedAtUtc: null,
  confirmedByUserId: null,
  confirmedAtUtc: null,
  updatedAtUtc: new Date(),
};
const finding: CodeReviewFindingDto = {
  level: "Major",
  file: "a",
  line: null,
  side: null,
  summary: "s",
  body: "body",
  occurrenceKey: "F1",
  findingRef: "12345.F1",
  semanticId: "test-finding-one",
  triage,
};
let cleanup = () => {};
afterEach(() => cleanup());
describe("FindingTriageControl", () => {
  it("shows a separate confirmation action for a proposal and emits only when clicked", async () => {
    const confirm = vi.fn();
    const root = document.createElement("div");
    document.body.append(root);
    const app = createApp({
      render: () =>
        h(FindingTriageControl, { finding, busy: false, onConfirm: confirm }),
    });
    app.mount(root);
    cleanup = () => {
      app.unmount();
      root.remove();
    };
    await nextTick();
    const button = [...root.querySelectorAll("button")].find((button) =>
      button.textContent?.includes("Confirm Proposal"),
    );
    expect(button).toBeDefined();
    expect(button!.getAttribute("aria-describedby")).toBe(
      "triage-context-12345.F1",
    );
    expect(confirm).not.toHaveBeenCalled();
    button!.click();
    expect(confirm).toHaveBeenCalledOnce();
  });
});

it("requires a nonblank reason before saving an open finding", async () => {
  const save = vi.fn();
  const root = document.createElement("div");
  document.body.append(root);
  const app = createApp({
    render: () =>
      h(FindingTriageControl, {
        finding: { ...finding, triage: null },
        busy: false,
        onSave: save,
      }),
  });
  app.mount(root);
  cleanup = () => {
    app.unmount();
    root.remove();
  };
  await nextTick();
  const trigger = [...root.querySelectorAll("button")].find((button) =>
    button.textContent?.includes("Triage"),
  );
  trigger!.click();
  await nextTick();
  const button = [...document.querySelectorAll("button")].find(
    (button) => button.textContent?.trim() === "Save",
  );
  expect(button).toBeDefined();
  expect(button!.disabled).toBe(true);
  const textarea = document.querySelector("textarea")!;
  const label = document.querySelector(`label[for="${textarea.id}"]`);
  expect(label?.textContent).toContain("Reason");
  expect(
    document.getElementById(textarea.getAttribute("aria-describedby")!)
      ?.textContent,
  ).toContain("A reason is required");
  textarea.value = "  verified reason  ";
  textarea.dispatchEvent(new Event("input", { bubbles: true }));
  await nextTick();
  expect(button!.disabled).toBe(false);
  button!.click();
  expect(save).toHaveBeenCalledWith(
    "FalsePositive",
    "verified reason",
    expect.any(String),
  );
});

it("allows a confirmed finding to be cleared without offering proposal confirmation", async () => {
  const clear = vi.fn();
  const root = document.createElement("div");
  document.body.append(root);
  const app = createApp({
    render: () =>
      h(FindingTriageControl, {
        finding: {
          ...finding,
          triage: { ...triage, approvalState: "Confirmed" },
        },
        busy: false,
        onClear: clear,
      }),
  });
  app.mount(root);
  cleanup = () => {
    app.unmount();
    root.remove();
  };
  await nextTick();
  expect(root.textContent).not.toContain("Confirm Proposal");
  const trigger = [...root.querySelectorAll("button")].find((button) =>
    button.textContent?.includes("Confirmed False Positive"),
  );
  trigger!.click();
  await nextTick();
  const button = [...document.querySelectorAll("button")].find(
    (button) => button.textContent?.trim() === "Clear",
  );
  expect(button).toBeDefined();
  button!.click();
  expect(clear).toHaveBeenCalledOnce();
});

it("keeps a failed save draft and resets after a successful clear", async () => {
  const { ref } = await import("vue");
  const state = ref<CodeReviewFindingDto>({ ...finding });
  const busy = ref(false);
  const successVersion = ref("");
  let requestId = "";
  const root = document.createElement("div");
  document.body.append(root);
  const app = createApp({
    render: () =>
      h(FindingTriageControl, {
        finding: state.value,
        busy: busy.value,
        successVersion: successVersion.value,
        onSave: (_disposition, _reason, id) => {
          requestId = id;
          busy.value = true;
        },
        onClear: (id) => {
          requestId = id;
          busy.value = true;
        },
      }),
  });
  app.mount(root);
  cleanup = () => {
    app.unmount();
    root.remove();
  };
  await nextTick();
  const trigger = () =>
    [...root.querySelectorAll("button")].find((button) =>
      button.textContent?.includes("Proposed"),
    )!;
  trigger().click();
  await nextTick();
  expect(document.querySelector("textarea")).toBeNull();
  [...document.querySelectorAll("button")]
    .find((button) => button.textContent?.trim() === "Edit reason")!
    .click();
  await nextTick();
  const input = document.querySelector("textarea")!;
  input.value = "unsaved draft";
  input.dispatchEvent(new Event("input", { bubbles: true }));
  await nextTick();
  [...document.querySelectorAll("button")]
    .find((button) => button.textContent?.trim() === "Save changes")!
    .click();
  await nextTick();
  busy.value = false;
  await nextTick();
  expect(document.querySelector("textarea")?.value).toBe("unsaved draft");
  trigger().click();
  await nextTick();
  trigger().click();
  await nextTick();
  expect(document.querySelector("textarea")?.value).toBe("unsaved draft");
  [...document.querySelectorAll("button")]
    .find((button) => button.textContent?.trim() === "Cancel")!
    .click();
  await nextTick();
  trigger().click();
  await nextTick();
  [...document.querySelectorAll("button")]
    .find((button) => button.textContent?.trim() === "Clear")!
    .click();
  await nextTick();
  state.value = { ...finding, triage: null };
  successVersion.value = requestId;
  busy.value = false;
  await nextTick();
  expect(
    [...root.querySelectorAll("button")]
      .find((button) => button.textContent?.trim() === "Triage")
      ?.getAttribute("aria-expanded"),
  ).toBe("false");
  [...root.querySelectorAll("button")]
    .find((button) => button.textContent?.trim() === "Triage")!
    .click();
  await nextTick();
  expect(document.querySelector("textarea")?.value).toBe("");
  expect(
    [...document.querySelectorAll("button")].find(
      (button) => button.textContent?.trim() === "Save",
    )?.disabled,
  ).toBe(true);
});

it("closes and refreshes its draft after a successful save", async () => {
  const { ref } = await import("vue");
  const state = ref<CodeReviewFindingDto>({ ...finding });
  const busy = ref(false);
  const successVersion = ref("");
  let requestId = "";
  const root = document.createElement("div");
  document.body.append(root);
  const app = createApp({
    render: () =>
      h(FindingTriageControl, {
        finding: state.value,
        busy: busy.value,
        successVersion: successVersion.value,
        onSave: (_disposition, _reason, id) => {
          requestId = id;
          busy.value = true;
        },
      }),
  });
  app.mount(root);
  cleanup = () => {
    app.unmount();
    root.remove();
  };
  await nextTick();
  [...root.querySelectorAll("button")]
    .find((button) => button.textContent?.includes("Proposed"))!
    .click();
  await nextTick();
  [...document.querySelectorAll("button")]
    .find((button) => button.textContent?.trim() === "Edit reason")!
    .click();
  await nextTick();
  const input = document.querySelector("textarea")!;
  input.value = "updated";
  input.dispatchEvent(new Event("input", { bubbles: true }));
  await nextTick();
  [...document.querySelectorAll("button")]
    .find((button) => button.textContent?.trim() === "Save changes")!
    .click();
  await nextTick();
  state.value = { ...finding, triage: { ...triage, currentReason: "updated" } };
  successVersion.value = requestId;
  busy.value = false;
  await nextTick();
  await nextTick();
  const trigger = [...root.querySelectorAll("button")].find((button) =>
    button.textContent?.includes("Proposed"),
  )!;
  expect(trigger.getAttribute("aria-expanded")).toBe("false");
  trigger.click();
  await nextTick();
  [...document.querySelectorAll("button")]
    .find((button) => button.textContent?.trim() === "Edit reason")!
    .click();
  await nextTick();
  expect(document.querySelector("textarea")?.value).toBe("updated");
});

it("does not discard an editor draft when another view saves the finding", async () => {
  const { ref } = await import("vue");
  const version = ref("");
  const root = document.createElement("div");
  document.body.append(root);
  const app = createApp({
    render: () =>
      h(FindingTriageControl, {
        finding,
        busy: false,
        successVersion: version.value,
      }),
  });
  app.mount(root);
  cleanup = () => {
    app.unmount();
    root.remove();
  };
  await nextTick();
  [...root.querySelectorAll("button")]
    .find((button) => button.textContent?.includes("Proposed"))!
    .click();
  await nextTick();
  [...document.querySelectorAll("button")]
    .find((button) => button.textContent?.trim() === "Edit reason")!
    .click();
  await nextTick();
  const input = document.querySelector("textarea")!;
  input.value = "local draft";
  input.dispatchEvent(new Event("input", { bubbles: true }));
  await nextTick();
  version.value = "other-request";
  await nextTick();
  expect(document.querySelector("textarea")?.value).toBe("local draft");
});
