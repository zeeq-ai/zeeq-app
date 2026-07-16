<template>
  <UDashboardPanel :id="id" :ui="{ body: bodyClass }">
    <!-- Header: shared navbar + optional toolbar -->
    <template #header>
      <UDashboardNavbar
        :title="title || resolvedTitle"
        :ui="{ right: 'gap-3' }"
      >
        <template #leading>
          <UDashboardSidebarCollapse />
        </template>

        <template #right>
          <PendingInvitationsSlideover
            :organizations="me?.organizations ?? []"
            :saving="invitationSaving"
            :pending-action="pendingInvitationAction"
            @accept="acceptPendingInvitation"
            @decline="declinePendingInvitation"
          />

          <!-- Findings cart: client-side draft carts, saved to become durable + MCP-retrievable. -->
          <CartSlideover
            :draft-carts="draftCarts"
            :active-draft-cart-id="activeDraftCartId"
            :saved-carts="savedCarts"
            :total-cart-count="totalCartCount"
            :total-item-count="totalItemCount"
            :compiling-cart-id="compilingCartId"
            :saving-cart-id="savingCartId"
            :max-carts-per-owner="MAX_CARTS_PER_OWNER"
            :max-items-per-cart="MAX_ITEMS_PER_CART"
            @new-draft="cartStore.createDraftCart()"
            @set-active="cartStore.setActiveDraftCart"
            @remove-draft-item="cartStore.removeDraftItem"
            @delete-draft="cartStore.deleteDraftCart"
            @save-draft="handleSaveDraftCart"
            @copy="handleCopyCart"
            @copy-to-draft="handleCopySavedCartToDraft"
            @delete-saved="cartStore.deleteSavedCart"
          />

          <UButton
            :icon="colorModeIcon"
            color="neutral"
            variant="ghost"
            square
            @click="toggleColorMode"
          />
        </template>
      </UDashboardNavbar>

      <!-- Toolbar row renders only when the page provides toolbar content -->
      <UDashboardToolbar v-if="$slots.toolbar">
        <slot name="toolbar" />
      </UDashboardToolbar>
    </template>

    <!-- Body: consumer provides page content -->
    <template #body>
      <slot />
    </template>
  </UDashboardPanel>
</template>

<script setup lang="ts">
import { storeToRefs } from "pinia";
import { useColorMode, useClipboard } from "@vueuse/core";
import { useRoute } from "vue-router";
import { useAppStore } from "@/stores/app-store";
import {
  useCartStore,
  MAX_CARTS_PER_OWNER,
  MAX_ITEMS_PER_CART,
} from "@/stores/cart-store";
import CartSlideover from "./CartSlideover.vue";

type PendingInvitationAction = {
  id: string;
  kind: "accept" | "decline";
};

defineProps<{
  id: string;
  title?: string;
  bodyClass?: string;
}>();

const toast = useToast();
const route = useRoute();
const colorMode = useColorMode();
const appStore = useAppStore();
const { user: me, invitationSaving } = storeToRefs(appStore);

const cartStore = useCartStore();
const {
  draftCarts,
  activeDraftCartId,
  savedCarts,
  totalCartCount,
  totalItemCount,
  compilingCartId,
  savingCartId,
} = storeToRefs(cartStore);
const { copy } = useClipboard({ legacy: true });

/** Application startup: load saved-cart metadata (no full finding body payload). */
onMounted(() => cartStore.loadSavedCarts());

const pendingInvitationAction = ref<PendingInvitationAction | null>(null);

/**
 * Falls back to route meta title when no explicit title prop is given.
 */
const resolvedTitle = computed(() => (route.meta.title as string) || "Zeeq");

const colorModeIcon = computed(() =>
  colorMode.value === "dark" ? "i-hugeicons-sun-03" : "i-hugeicons-moon-02",
);

function toggleColorMode() {
  colorMode.value = colorMode.value === "dark" ? "light" : "dark";
}

/** Accepts a pending invitation from the shared shell slideover. */
async function acceptPendingInvitation(
  invitationId: string,
  organizationId: string,
) {
  pendingInvitationAction.value = { id: invitationId, kind: "accept" };

  try {
    await appStore.acceptInvitation(invitationId, organizationId);
    toast.add({
      title: "Invitation accepted",
      icon: "i-hugeicons-tick-02",
      color: "success",
    });
  } catch (err: unknown) {
    showInvitationError(err, "Could not accept invitation.");
  } finally {
    pendingInvitationAction.value = null;
  }
}

/** Declines a pending invitation from the shared shell slideover. */
async function declinePendingInvitation(invitationId: string) {
  pendingInvitationAction.value = { id: invitationId, kind: "decline" };

  try {
    await appStore.declineInvitation(invitationId);
    toast.add({
      title: "Invitation declined",
      icon: "i-hugeicons-tick-02",
      color: "success",
    });
  } catch (err: unknown) {
    showInvitationError(err, "Could not decline invitation.");
  } finally {
    pendingInvitationAction.value = null;
  }
}

/** Shows invitation mutation errors in the shared Nuxt UI toast surface. */
function showInvitationError(err: unknown, fallback: string) {
  toast.add({
    title: "Invitation update failed",
    description: err instanceof Error ? err.message : fallback,
    icon: "i-hugeicons-alert-02",
    color: "error",
  });
}

// ── Cart handlers ──────────────────────────────────────────────────────

/** Copies saved cart instructions text to clipboard. */
async function handleCopyCart(cartId: string) {
  try {
    const text = await cartStore.getCartText(cartId);
    await copy(text);
    toast.add({
      title: "Cart instructions copied",
      icon: "i-hugeicons-tick-02",
      color: "success",
    });
  } catch (err) {
    toast.add({
      title: "Could not compile cart text",
      description: err instanceof Error ? err.message : undefined,
      icon: "i-hugeicons-alert-02",
      color: "error",
    });
  }
}

/** Saves a draft cart server-side and copies the instructions text. */
async function handleSaveDraftCart(cartId: string) {
  const cart = draftCarts.value.find((c) => c.id === cartId);
  try {
    const text = await cartStore.saveCart(cartId);
    if (text) {
      await copy(text);
    }
    toast.add({
      title: `Cart ${cart?.name ?? ""} saved and copied`,
      icon: "i-hugeicons-tick-02",
      color: "success",
    });
  } catch (err) {
    toast.add({
      title: "Could not save cart",
      description: err instanceof Error ? err.message : undefined,
      icon: "i-hugeicons-alert-02",
      color: "error",
    });
  }
}

/** Copies an immutable saved cart into a new local draft. */
async function handleCopySavedCartToDraft(cartId: string) {
  try {
    const draft = await cartStore.copySavedCartToDraft(cartId);
    toast.add({
      title: `Copied to draft ${draft.name}`,
      icon: "i-hugeicons-tick-02",
      color: "success",
    });
  } catch (err) {
    toast.add({
      title: "Could not copy cart to draft",
      description: err instanceof Error ? err.message : undefined,
      icon: "i-hugeicons-alert-02",
      color: "error",
    });
  }
}
</script>
