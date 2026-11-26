import { afterEach, describe, expect, it, vi } from "vitest";
import { createApp, h, nextTick } from "vue";
import { createMemoryHistory, createRouter } from "vue-router";
import FindingTriageLink from "./FindingTriageLink.vue";
const mocks = vi.hoisted(() => ({
  load: vi.fn(),
  confirm: vi.fn(),
  reopen: vi.fn(),
  switchOrg: vi.fn(),
}));
vi.mock("@/stores/app-store", () => ({
  useAppStore: () => ({
    user: { organizationId: "org", organizations: [{ id: "org" }] },
    switchOrganization: mocks.switchOrg,
  }),
}));
vi.mock("@/stores/code-review-store", () => ({
  useCodeReviewStore: () => ({
    loadFindingTriage: mocks.load,
    confirmFindingTriage: mocks.confirm,
    reopenFindingTriage: mocks.reopen,
  }),
}));
const snapshot = (id: string) => ({
  sourceReviewExpired: true,
  singleReviewUrl: null,
  triage: {
    id,
    findingSummary: `Finding ${id}`,
    facet: "Security",
    semanticId: "test-finding-one",
    disposition: "FalsePositive",
    approvalState: "Proposed",
    currentReason: "reason",
  },
});
const tick = async () => {
  await Promise.resolve();
  await nextTick();
  await Promise.resolve();
  await nextTick();
};
let cleanup = () => {};
afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});
describe("expired finding triage link", () => {
  it("clears old content on navigation, ignores stale loads, and displays confirmation", async () => {
    let resolveFirst!: (value: unknown) => void;
    const first = new Promise((resolve) => {
      resolveFirst = resolve;
    });
    mocks.load.mockImplementation((id: string) =>
      id === "first" ? first : Promise.resolve(snapshot(id)),
    );
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: "/triages/:orgId/:triageId", component: FindingTriageLink },
      ],
    });
    await router.push("/triages/org/first");
    await router.isReady();
    const root = document.createElement("div");
    document.body.append(root);
    // Use the actual RouterView component to retain the same instance on parameter changes.
    const { RouterView } = await import("vue-router");
    const mounted = createApp({ render: () => h(RouterView) });
    mounted.use(router);
    mounted.mount(root);
    cleanup = () => {
      mounted.unmount();
      root.remove();
    };
    await tick();
    await router.push("/triages/org/second");
    await tick();
    expect(root.textContent).toContain("Finding second");
    expect(root.textContent).toContain("False positive");
    expect(root.textContent).toContain("Proposed");
    resolveFirst(snapshot("first"));
    await tick();
    expect(root.textContent).not.toContain("Finding first");
    mocks.confirm.mockRejectedValueOnce(new Error("Try again"));
    [...root.querySelectorAll("button")]
      .find((button) => button.textContent?.includes("Confirm Proposal"))!
      .click();
    await tick();
    expect(root.textContent).toContain("Try again");
    expect(root.textContent).toContain("reason");
    expect(root.textContent).toContain("Confirm Proposal");
    mocks.confirm.mockResolvedValue({
      ...snapshot("second").triage,
      approvalState: "Confirmed",
    });
    const confirmButton = [...root.querySelectorAll("button")].find((button) =>
      button.textContent?.includes("Confirm Proposal"),
    )!;
    confirmButton.click();
    confirmButton.click();
    await tick();
    expect(mocks.confirm).toHaveBeenCalledTimes(2);
    expect(root.textContent).toContain("Confirmed");
    expect(mocks.confirm).toHaveBeenCalledWith("second", {
      expectedDisposition: "FalsePositive",
      reason: null,
    });
    mocks.reopen.mockResolvedValue({
      ...snapshot("second").triage,
      approvalState: "Open",
      disposition: null,
    });
    [...root.querySelectorAll("button")]
      .find((button) => button.textContent?.includes("Reopen finding"))!
      .click();
    await tick();
    expect(mocks.reopen).toHaveBeenCalledWith("second");
    expect(root.textContent).toContain("Open");
    mocks.load.mockReturnValue(new Promise(() => {}));
    await router.push("/triages/org/third");
    await tick();
    expect(root.textContent).not.toContain("Finding second");
    expect(root.textContent).not.toContain("Confirm Proposal");
  });
});
