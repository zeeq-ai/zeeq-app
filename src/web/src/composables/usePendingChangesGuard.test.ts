import { describe, expect, it, vi } from "vitest";
import { ref } from "vue";

import { usePendingChangesGuard } from "./usePendingChangesGuard";

describe("usePendingChangesGuard", () => {
  it("runs the action immediately when there are no pending changes", async () => {
    const hasChanges = ref(false);
    const save = vi.fn();
    const action = vi.fn();
    const guard = usePendingChangesGuard({ hasChanges, save });

    const blocked = await guard.confirmBefore(action);

    expect(blocked).toBe(false);
    expect(action).toHaveBeenCalledOnce();
    expect(guard.open.value).toBe(false);
  });

  it("defers the action while pending changes are present", async () => {
    const hasChanges = ref(true);
    const save = vi.fn();
    const action = vi.fn();
    const guard = usePendingChangesGuard({ hasChanges, save });

    const blocked = await guard.confirmBefore(action);

    expect(blocked).toBe(true);
    expect(action).not.toHaveBeenCalled();
    expect(guard.open.value).toBe(true);

    await guard.discardChanges();

    expect(action).toHaveBeenCalledOnce();
    expect(guard.open.value).toBe(false);
  });

  it("saves without replaying the pending navigation action", async () => {
    const hasChanges = ref(true);
    const save = vi.fn();
    const action = vi.fn();
    const guard = usePendingChangesGuard({ hasChanges, save });

    await guard.confirmBefore(action);
    await guard.saveChanges();

    expect(save).toHaveBeenCalledOnce();
    expect(action).not.toHaveBeenCalled();
    expect(guard.pendingAction.value).toBeNull();
  });
});
