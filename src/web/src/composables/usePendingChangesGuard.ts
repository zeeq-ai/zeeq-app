import { ref, toValue, type MaybeRefOrGetter } from "vue";

type PendingAction = () => void | Promise<void>;

type PendingChangesGuardOptions = {
  hasChanges: MaybeRefOrGetter<boolean>;
  save: () => void | Promise<void>;
};

/**
 * Defers navigation-like actions while a local editor has unsaved changes.
 * The caller renders the confirmation UI and chooses the wording.
 * `saveChanges` intentionally does not replay the pending action; diff-based
 * save flows need the user to confirm the reviewed change before navigating.
 */
export function usePendingChangesGuard(options: PendingChangesGuardOptions) {
  const open = ref(false);
  const pendingAction = ref<PendingAction | null>(null);

  async function confirmBefore(action: PendingAction) {
    if (!toValue(options.hasChanges)) {
      await action();
      return false;
    }

    pendingAction.value = action;
    open.value = true;
    return true;
  }

  async function discardChanges() {
    // NOTE: Discard intentionally replays the action captured at the moment the
    // modal opened; save/cancel paths clear it instead of navigating.
    const action = pendingAction.value;
    pendingAction.value = null;
    open.value = false;

    if (action) {
      await action();
    }
  }

  async function saveChanges() {
    pendingAction.value = null;
    open.value = false;
    await options.save();
  }

  function cancelPendingAction() {
    pendingAction.value = null;
    open.value = false;
  }

  return {
    open,
    pendingAction,
    confirmBefore,
    discardChanges,
    saveChanges,
    cancelPendingAction,
  };
}
