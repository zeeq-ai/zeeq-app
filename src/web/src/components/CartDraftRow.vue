<template>
  <!--
  Collapsible row for a single draft (local-only) cart.
  The active-basket icon marks which draft receives new findings;
  clicking it emits setActive. Save cart persists server-side and
  copies the MCP instruction. The item list expands inline to let
  the user remove individual findings.
  -->
  <CartRowShell
    v-model:open="open"
    :name="cart.name"
    :date-label="formatDate(cart.createdAtUtc)"
    badge-label="Draft"
    :count-label="`${cart.items.length}/${maxItemsPerCart}`"
  >
    <template #leading>
      <!-- Active-basket toggle: marks this draft as the add-to target. -->
      <UTooltip
        :text="
          cart.id === activeDraftCartId
            ? 'Active \u2014 new findings go here'
            : 'Set as active cart'
        "
      >
        <UButton
          :icon="
            cart.id === activeDraftCartId
              ? 'i-hugeicons-shopping-basket-favorite-01'
              : 'i-hugeicons-shopping-basket-01'
          "
          :color="cart.id === activeDraftCartId ? 'primary' : 'neutral'"
          variant="ghost"
          size="xs"
          square
          :aria-label="
            cart.id === activeDraftCartId ? 'Active cart' : 'Set as active cart'
          "
          @click.stop="emits('setActive', cart.id)"
        />
      </UTooltip>
    </template>

    <template #actions>
      <!-- Save cart: persists server-side and copies the MCP agent instruction. -->
      <UTooltip text="Save cart and copy agent instructions">
        <UButton
          icon="i-hugeicons-checkmark-circle-02"
          label="Save cart"
          color="primary"
          variant="soft"
          size="xs"
          :loading="savingCartId === cart.id"
          :disabled="cart.items.length === 0"
          aria-label="Save cart"
          @click.stop="emits('saveDraft', cart.id)"
        />
      </UTooltip>

      <!-- Discard this draft entirely (client-only, no server call). -->
      <UTooltip text="Discard this draft">
        <UButton
          icon="i-hugeicons-delete-02"
          color="neutral"
          variant="ghost"
          size="xs"
          square
          aria-label="Delete draft cart"
          @click.stop="emits('deleteDraft', cart.id)"
        />
      </UTooltip>
    </template>

    <template #content>
      <!-- Inline finding list; each row removes the item from this draft. -->
      <CartFindingSummaryList :items="cart.items" interactive>
        <template #actions="{ item }">
          <UTooltip text="Remove from cart">
            <UButton
              icon="i-hugeicons-delete-02"
              color="neutral"
              variant="ghost"
              size="xs"
              square
              aria-label="Remove finding"
              @click="emits('removeDraftItem', cart.id, item.hash)"
            />
          </UTooltip>
        </template>
      </CartFindingSummaryList>
    </template>
  </CartRowShell>
</template>

<script setup lang="ts">
import type { DraftCart } from "@/stores/cart-store";
import CartFindingSummaryList from "./CartFindingSummaryList.vue";
import CartRowShell from "./CartRowShell.vue";

/** Two-way open/close state driven by the parent (single-open accordion). */
const open = defineModel<boolean>("open", { default: false });

const props = defineProps<{
  cart: DraftCart;
  /** Which draft cart is currently the add-to target. */
  activeDraftCartId: string | null;
  /** Cart id currently being saved; drives the loading spinner. */
  savingCartId: string | null;
  maxItemsPerCart: number;
}>();

const emits = defineEmits<{
  setActive: [cartId: string];
  removeDraftItem: [cartId: string, hash: string];
  deleteDraft: [cartId: string];
  saveDraft: [cartId: string];
}>();

function formatDate(isoTimestamp: string): string {
  return new Date(isoTimestamp).toLocaleString();
}
</script>
