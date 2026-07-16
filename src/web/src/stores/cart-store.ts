import { defineStore, acceptHMRUpdate } from "pinia";
import { useStorage } from "@vueuse/core";
import { Carts } from "@/api/generated";
import { useAppStore } from "@/stores/app-store";
import type { CartResponse, SaveCartItemRequest } from "@/api/generated";
import { generateCartName } from "@/composables/generateCartName";
import { computeFindingContentHash } from "@/composables/useFindingContentHash";

/** One finding inside a client-side draft cart — the local mirror of SaveCartItemRequest. */
export type DraftCartItem = {
  hash: string;
  title: string;
  criticality: string;
  file: string;
  line?: number | null;
  side?: string | null;
  summary: string;
  body: string;
  ownerQualifiedRepoName: string;
  pullRequestNumber: number;
  facet: string;
  agent: string;
  annotation: string | null;
  addedAtUtc: string;
};

/** A cart that has not yet been saved — lives only in browser storage. */
export type DraftCart = {
  id: string;
  name: string;
  createdAtUtc: string;
  items: DraftCartItem[];
};

export const MAX_CARTS_PER_OWNER = 5;
export const MAX_ITEMS_PER_CART = 10;
export const MAX_ANNOTATION_LENGTH = 500;

/**
 * Findings-cart store. Drafts are pure client state (nothing is sent to the
 * server until Save cart); saved carts are summary-only immutable server state
 * fetched from the API. Only root view components (ZeeqView.vue,
 * PullRequestReviews.vue) consume this store directly.
 */
export const useCartStore = defineStore("cart", () => {
  const appStore = useAppStore();
  const orgId = computed(() => appStore.user?.organizationId ?? "");

  const draftCarts = useStorage<DraftCart[]>("zeeq:cart-drafts", []);
  const activeDraftCartId = useStorage<string | null>(
    "zeeq:active-draft-cart",
    null,
  );

  const savedCarts = ref<CartResponse[]>([]);
  const loadingSavedCarts = ref(false);
  const compilingCartId = ref<string | null>(null);
  const savingCartId = ref<string | null>(null);

  /** The currently Active draft, which receives all add/remove toggle actions. */
  const activeDraftCart = computed(
    () =>
      draftCarts.value.find((cart) => cart.id === activeDraftCartId.value) ??
      null,
  );

  /** Combined count across localStorage drafts and server-side saved carts. */
  const totalCartCount = computed(
    () => draftCarts.value.length + savedCarts.value.length,
  );

  /** Total findings across all carts (draft + saved). */
  const totalItemCount = computed(
    () =>
      draftCarts.value.reduce((sum, cart) => sum + cart.items.length, 0) +
      savedCarts.value.reduce((sum, cart) => sum + +cart.itemCount, 0),
  );

  /**
   * Hashes for the Active draft only. Saved carts are immutable display/copy/
   * delete rows and must not participate in "In cart" badge matching.
   */
  const cartContentHashes = computed(() => {
    const hashes = new Set<string>();
    const activeDraft = activeDraftCart.value;
    for (const item of activeDraft?.items ?? []) {
      hashes.add(item.hash);
    }
    return hashes;
  });

  // ── Saved-cart loading (metadata-only — no full body payload) ────────

  /** Loads saved-cart metadata at application startup. */
  async function loadSavedCarts() {
    loadingSavedCarts.value = true;
    try {
      const response = await Carts.listCarts(orgId.value);
      savedCarts.value = response.items;
    } finally {
      loadingSavedCarts.value = false;
    }
  }

  // ── Draft cart lifecycle ────────────────────────────────────────────

  /** Starts a new empty draft cart and makes it Active. */
  function createDraftCart(): DraftCart {
    if (totalCartCount.value >= MAX_CARTS_PER_OWNER) {
      throw new CartLimitError();
    }

    const name = generateCartName();
    const draft: DraftCart = {
      id: name,
      name,
      createdAtUtc: new Date().toISOString(),
      items: [],
    };

    draftCarts.value = [draft, ...draftCarts.value];
    activeDraftCartId.value = draft.id;

    return draft;
  }

  /** Points Active at any existing draft. */
  function setActiveDraftCart(cartId: string) {
    if (draftCarts.value.some((cart) => cart.id === cartId)) {
      activeDraftCartId.value = cartId;
    }
  }

  /**
   * Toggles a finding into/out of the Active draft (auto-creating one if
   * none is set). Always local browser state; saved carts are immutable.
   */
  async function toggleFinding(
    finding: {
      level: string;
      file: string;
      line?: number | null;
      side?: string | null;
      summary: string;
      body: string;
    },
    reviewer: { facet: string; agent: string },
    review: { ownerQualifiedRepoName: string; pullRequestNumber: number },
    annotation: string | null,
  ): Promise<{ added: boolean; cartName: string }> {
    const active = activeDraftCart.value ?? createDraftCart();
    const hash = await computeFindingContentHash(finding);
    const existingIndex = active.items.findIndex((item) => item.hash === hash);

    if (existingIndex >= 0) {
      draftCarts.value = draftCarts.value.map((cart) =>
        cart.id === active.id
          ? { ...cart, items: cart.items.filter((_, i) => i !== existingIndex) }
          : cart,
      );
      return { added: false, cartName: active.name };
    }

    if (active.items.length >= MAX_ITEMS_PER_CART) {
      throw new CartItemLimitError(active.name);
    }

    const annotationNormalized = annotation?.trim()
      ? annotation.trim().slice(0, MAX_ANNOTATION_LENGTH)
      : null;

    const newItem: DraftCartItem = {
      hash,
      title: finding.summary,
      criticality: finding.level,
      file: finding.file,
      line: finding.line,
      side: finding.side,
      summary: finding.summary,
      body: finding.body,
      ownerQualifiedRepoName: review.ownerQualifiedRepoName,
      pullRequestNumber: review.pullRequestNumber,
      facet: reviewer.facet,
      agent: reviewer.agent,
      annotation: annotationNormalized,
      addedAtUtc: new Date().toISOString(),
    };
    draftCarts.value = draftCarts.value.map((cart) =>
      cart.id === active.id
        ? { ...cart, items: [...cart.items, newItem] }
        : cart,
    );

    return { added: true, cartName: active.name };
  }

  /** Removes one item from a draft cart by hash (for the expanded listbox). */
  function removeDraftItem(cartId: string, hash: string) {
    draftCarts.value = draftCarts.value.map((cart) =>
      cart.id === cartId
        ? { ...cart, items: cart.items.filter((item) => item.hash !== hash) }
        : cart,
    );
  }

  /** Deletes a local draft cart. If it was Active, Active is cleared. */
  function deleteDraftCart(cartId: string) {
    draftCarts.value = draftCarts.value.filter((cart) => cart.id !== cartId);
    if (activeDraftCartId.value === cartId) {
      activeDraftCartId.value = null;
    }
  }

  // ── Saved-cart operations ───────────────────────────────────────────

  /**
   * Persists a draft server-side. On success the draft is removed from local
   * storage, the saved row is added, and the cart text is returned.
   */
  async function saveCart(cartId: string): Promise<string | null> {
    const draft = draftCarts.value.find((cart) => cart.id === cartId);
    if (!draft || draft.items.length === 0) return null;

    savingCartId.value = cartId;
    try {
      const items: SaveCartItemRequest[] = draft.items.map((item) => ({
        hash: item.hash,
        title: item.title,
        criticality: item.criticality,
        file: item.file,
        line: item.line ?? null,
        side: item.side ?? null,
        summary: item.summary,
        body: item.body,
        ownerQualifiedRepoName: item.ownerQualifiedRepoName,
        pullRequestNumber: item.pullRequestNumber,
        facet: item.facet,
        agent: item.agent,
        annotation: item.annotation,
        addedAtUtc: new Date(item.addedAtUtc),
      }));

      const saved = await Carts.saveCart(orgId.value, {
        id: draft.id,
        name: draft.name,
        createdAtUtc: new Date(draft.createdAtUtc),
        items,
      });

      savedCarts.value = [saved, ...savedCarts.value];
      deleteDraftCart(cartId);
      return await getCartText(saved.id);
    } finally {
      savingCartId.value = null;
    }
  }

  /** Deletes a saved cart server-side and removes it from local state. */
  async function deleteSavedCart(cartId: string) {
    await Carts.deleteCart(orgId.value, cartId);
    savedCarts.value = savedCarts.value.filter((cart) => cart.id !== cartId);
  }

  /**
   * Copies a saved cart's full items into a new local draft with a new id
   * and generated name. The saved cart remains unchanged.
   */
  async function copySavedCartToDraft(cartId: string): Promise<DraftCart> {
    const source = await Carts.getCartCopySource(orgId.value, cartId);
    const name = generateCartName();
    const draft: DraftCart = {
      id: name,
      name,
      createdAtUtc: new Date().toISOString(),
      items: source.items.map((item) => ({
        hash: item.hash,
        title: item.title,
        criticality: item.criticality,
        file: item.file,
        line: typeof item.line === "number" ? item.line : null,
        side: item.side ?? null,
        summary: item.summary,
        body: item.body,
        ownerQualifiedRepoName: item.ownerQualifiedRepoName,
        pullRequestNumber: +item.pullRequestNumber,
        facet: item.facet,
        agent: item.agent,
        annotation: item.annotation ?? null,
        addedAtUtc:
          typeof item.addedAtUtc === "string"
            ? item.addedAtUtc
            : new Date().toISOString(),
      })),
    };

    draftCarts.value = [draft, ...draftCarts.value];
    activeDraftCartId.value = draft.id;
    return draft;
  }

  /**
   * Compiles and returns the latest saved cart's agent-ready text. Caller
   * drives the loading UI via `compilingCartId`.
   */
  async function getCartText(cartId: string): Promise<string> {
    compilingCartId.value = cartId;
    try {
      const response = await Carts.getCartText(orgId.value, cartId);
      return response.text;
    } finally {
      compilingCartId.value = null;
    }
  }

  return {
    draftCarts,
    activeDraftCartId,
    activeDraftCart,
    savedCarts,
    loadingSavedCarts,
    compilingCartId,
    savingCartId,
    totalCartCount,
    totalItemCount,
    cartContentHashes,
    loadSavedCarts,
    createDraftCart,
    setActiveDraftCart,
    toggleFinding,
    removeDraftItem,
    deleteDraftCart,
    saveCart,
    deleteSavedCart,
    copySavedCartToDraft,
    getCartText,
  };
});

/** Thrown when the 5-cart cap is already reached. */
export class CartLimitError extends Error {
  constructor() {
    super(
      `You already have ${MAX_CARTS_PER_OWNER} carts. Delete one to add more.`,
    );
  }
}

/** Thrown when the Active draft already has 10 items. */
export class CartItemLimitError extends Error {
  constructor(cartName: string) {
    super(
      `Cart ${cartName} is full (${MAX_ITEMS_PER_CART}/${MAX_ITEMS_PER_CART} findings).`,
    );
  }
}

if (import.meta.hot) {
  import.meta.hot.accept(acceptHMRUpdate(useCartStore, import.meta.hot));
}
