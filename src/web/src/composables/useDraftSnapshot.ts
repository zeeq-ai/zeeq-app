import {
  computed,
  ref,
  watch,
  type Ref,
  type WatchSource,
  type WatchStopHandle,
} from "vue";

type DraftSnapshotOptions<T> = {
  clone?: (value: T) => T;
  serialize?: (value: T) => string;
  source?: WatchSource<T>;
};

const defaultClone = <T>(value: T): T => structuredClone(value);
const defaultSerialize = <T>(value: T): string => JSON.stringify(value);

/**
 * Tracks an editable draft against a serialized baseline.
 * Components own the draft shape; this composable owns dirty/reset semantics.
 * Use `resetToBaseline` after a successful save or prop reseed, and
 * `resetDraft` when the user wants to discard back to that last saved value.
 */
export function useDraftSnapshot<T>(
  initialValue: T,
  options: DraftSnapshotOptions<T> = {},
) {
  const clone = options.clone ?? defaultClone;
  const serialize = options.serialize ?? defaultSerialize;
  const draft = ref(clone(initialValue)) as Ref<T>;
  const savedValue = ref(clone(draft.value)) as Ref<T>;
  const savedSnapshot = ref(serialize(savedValue.value));
  let stopSourceWatch: WatchStopHandle | null = null;

  const currentSnapshot = computed(() => serialize(draft.value));
  const dirty = computed(() => currentSnapshot.value !== savedSnapshot.value);

  /** Replaces visible draft state; optionally promotes it to the saved baseline. */
  function replaceDraft(value: T, updateBaseline = false) {
    draft.value = clone(value);

    if (updateBaseline) {
      savedValue.value = clone(draft.value);
      savedSnapshot.value = serialize(draft.value);
    }
  }

  /** Discards local edits without changing the saved baseline. */
  function resetDraft() {
    draft.value = clone(savedValue.value);
  }

  /** Sets a new saved baseline and clears dirty state for the provided value. */
  function resetToBaseline(value: T) {
    replaceDraft(value, true);
  }

  if (options.source) {
    stopSourceWatch = watch(
      options.source,
      (value) => {
        resetToBaseline(value);
      },
      { immediate: true },
    );
  }

  return {
    draft,
    savedValue,
    savedSnapshot,
    currentSnapshot,
    dirty,
    replaceDraft,
    resetDraft,
    resetToBaseline,
    stopSourceWatch,
  };
}
