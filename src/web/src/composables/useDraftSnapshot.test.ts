import { describe, expect, it } from "vitest";
import { nextTick } from "vue";

import { useDraftSnapshot } from "./useDraftSnapshot";

type Draft = {
  name: string;
  nested: {
    enabled: boolean;
  };
};

const cloneDraft = (value: Draft): Draft => ({
  name: value.name,
  nested: { ...value.nested },
});

describe("useDraftSnapshot", () => {
  it("tracks dirty state against the saved baseline", async () => {
    const snapshot = useDraftSnapshot(
      { name: "Original", nested: { enabled: true } },
      { clone: cloneDraft },
    );

    expect(snapshot.dirty.value).toBe(false);

    snapshot.draft.value.name = "Changed";
    await nextTick();

    expect(snapshot.dirty.value).toBe(true);
  });

  it("resets the baseline and draft together", async () => {
    const snapshot = useDraftSnapshot(
      { name: "Original", nested: { enabled: true } },
      { clone: cloneDraft },
    );

    snapshot.draft.value.name = "Changed";
    await nextTick();
    snapshot.resetToBaseline({ name: "Saved", nested: { enabled: false } });

    expect(snapshot.draft.value).toEqual({
      name: "Saved",
      nested: { enabled: false },
    });
    expect(snapshot.dirty.value).toBe(false);
  });

  it("resets draft edits back to the saved baseline", async () => {
    const snapshot = useDraftSnapshot(
      { name: "Original", nested: { enabled: true } },
      { clone: cloneDraft },
    );

    snapshot.draft.value.name = "Changed";
    snapshot.draft.value.nested.enabled = false;
    await nextTick();

    snapshot.resetDraft();

    expect(snapshot.draft.value).toEqual({
      name: "Original",
      nested: { enabled: true },
    });
    expect(snapshot.dirty.value).toBe(false);
  });

  it("clones baseline values so later edits do not mutate the source", () => {
    const source = { name: "Original", nested: { enabled: true } };
    const snapshot = useDraftSnapshot(source, { clone: cloneDraft });

    snapshot.draft.value.nested.enabled = false;

    expect(source.nested.enabled).toBe(true);
  });
});
