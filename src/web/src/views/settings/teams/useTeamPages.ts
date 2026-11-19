import { onScopeDispose, ref, shallowRef, watch, type Ref } from "vue";

interface Page<T> {
  items: T[];
  nextCursor: string | null;
}

/** Loads a scoped page and discards results from a superseded search or organization. */
export function useTeamPages<T>(
  scope: Readonly<Ref<string | null>>,
  fetchPage: (
    after: string | undefined,
    signal: AbortSignal,
  ) => Promise<Page<T>>,
) {
  const items = shallowRef<T[]>([]);
  const nextCursor = ref<string | null>(null);
  const loading = ref(false);
  const error = ref<string | null>(null);
  let currentRequest: AbortController | undefined;

  async function load(after?: string) {
    currentRequest?.abort();
    if (!after) {
      items.value = [];
      nextCursor.value = null;
    }

    if (!scope.value) {
      loading.value = false;
      error.value = null;
      return;
    }

    const request = new AbortController();
    currentRequest = request;
    loading.value = true;
    error.value = null;
    try {
      const page = await fetchPage(after, request.signal);
      if (!request.signal.aborted) {
        items.value = after ? [...items.value, ...page.items] : page.items;
        nextCursor.value = page.nextCursor;
      }
    } catch (cause) {
      if (!request.signal.aborted) {
        error.value =
          cause instanceof Error ? cause.message : "Could not load results.";
      }
    } finally {
      if (!request.signal.aborted) {
        loading.value = false;
      }
    }
  }

  watch(scope, () => void load(), { immediate: true });
  onScopeDispose(() => currentRequest?.abort());

  function loadMore() {
    if (nextCursor.value && !loading.value) {
      return load(nextCursor.value);
    }
  }

  return { items, nextCursor, loading, error, reload: () => load(), loadMore };
}
