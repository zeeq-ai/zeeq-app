// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { useCartStore } from "./cart-store";
import { Carts } from "@/api/generated";

vi.mock("@/composables/useFindingContentHash", () => ({
  computeFindingContentHash: async () => "stable-hash",
}));

describe("cart finding identity", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });
  it("retains source identity through draft and save while preserving content dedupe", async () => {
    const save = vi
      .spyOn(Carts, "saveCart")
      .mockResolvedValue({ id: "saved" } as never);
    vi.spyOn(Carts, "getCartText").mockResolvedValue({ text: "" } as never);
    const store = useCartStore();
    const finding = {
      level: "Major",
      file: "file",
      summary: "summary",
      body: "body",
      occurrenceKey: "F2",
      semanticId: "test-finding-two",
    };
    await store.toggleFinding(
      finding,
      { facet: "Security", agent: "agent" },
      { id: "review", ownerQualifiedRepoName: "a/b", pullRequestNumber: 1 },
      null,
    );
    const draft = store.activeDraftCart!;
    expect(draft.items[0]).toMatchObject({
      hash: "stable-hash",
      sourceReviewId: "review",
      occurrenceKey: "F2",
      semanticId: "test-finding-two",
    });
    await store.saveCart(draft.id);
    expect(save.mock.calls[0]![1].items[0]).toMatchObject({
      sourceReviewId: "review",
      occurrenceKey: "F2",
      semanticId: "test-finding-two",
    });
    vi.restoreAllMocks();
  });
});

it("preserves source identity when copying a saved cart and saving it again", async () => {
  setActivePinia(createPinia());
  vi.spyOn(Carts, "getCartCopySource").mockResolvedValue({
    items: [
      {
        hash: "hash",
        title: "title",
        criticality: "Major",
        file: "a",
        line: null,
        side: null,
        summary: "summary",
        body: "body",
        ownerQualifiedRepoName: "a/b",
        pullRequestNumber: 1,
        facet: "Security",
        agent: "agent",
        annotation: null,
        addedAtUtc: new Date().toISOString(),
        sourceReviewId: "review",
        occurrenceKey: "F2",
        semanticId: "test-finding-two",
      },
    ],
  } as never);
  const save = vi
    .spyOn(Carts, "saveCart")
    .mockResolvedValue({ id: "saved" } as never);
  vi.spyOn(Carts, "getCartText").mockResolvedValue({ text: "" } as never);
  const store = useCartStore();
  const draft = await store.copySavedCartToDraft("saved");
  await store.saveCart(draft.id);
  expect(save.mock.calls[0]![1].items[0]).toMatchObject({
    sourceReviewId: "review",
    occurrenceKey: "F2",
    semanticId: "test-finding-two",
  });
});
