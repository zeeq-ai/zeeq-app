<template>
  <!-- Navbar trigger: shopping-basket icon with combined draft + saved item count chip. -->
  <UTooltip text="Findings cart">
    <UButton
      color="neutral"
      variant="ghost"
      square
      aria-label="Findings cart"
      @click="
        () => {
          isOpen = true;
        }
      "
    >
      <UChip
        color="primary"
        size="3xl"
        :show="totalItemCount > 0"
        :text="totalItemCount"
      >
        <UIcon
          name="i-hugeicons-shopping-basket-check-out-01"
          class="size-5 shrink-0"
        />
      </UChip>
    </UButton>
  </UTooltip>

  <!-- Right-side slideover with draft carts (mutable) and saved carts (immutable). -->
  <USlideover
    v-model:open="isOpen"
    side="right"
    title="Findings cart"
    :ui="{ content: 'w-160 sm:max-w-160' }"
  >
    <template #body>
      <div class="grid gap-4">
        <!-- How-to explainer shown at all times. -->
        <UAlert
          color="neutral"
          variant="subtle"
          icon="i-hugeicons-information-circle"
        >
          <template #description>
            Add findings from any code review to a draft cart, then save it to
            generate a prompt for an AI agent. The agent uses those findings to
            guide your fixes. You can have up to
            {{ maxCartsPerOwner }} saved carts with {{ maxItemsPerCart }}
            findings each — manage them manually by copying or reopening as a
            draft. Saved carts are automatically removed after 7 days.
          </template>
        </UAlert>

        <!-- Empty state when no carts exist. -->
        <div
          v-if="totalCartCount === 0"
          class="flex min-h-48 flex-col items-center justify-center gap-2"
        >
          <UIcon
            name="i-hugeicons-shopping-basket-check-out-01"
            class="size-14 text-muted"
          />
          <p class="text-sm font-medium text-highlighted">
            No findings carts yet
          </p>
          <p class="text-sm text-muted">
            Add a finding from a code review to start one.
          </p>
        </div>

        <!-- Cart lists: drafts first (mutable), then saved (immutable). -->
        <div v-else class="grid gap-2">
          <!--
          Draft cart rows — each manages its own collapsible via v-model:open.
          openCartId enforces single-open accordion behavior across all rows.
          -->
          <CartDraftRow
            v-for="cart in draftCarts"
            :key="cart.id"
            :open="openCartId === cart.id"
            :cart="cart"
            :active-draft-cart-id="activeDraftCartId"
            :saving-cart-id="savingCartId"
            :max-items-per-cart="maxItemsPerCart"
            @update:open="handleToggleExpand(cart.id, 'draft', $event)"
            @set-active="emits('setActive', $event)"
            @remove-draft-item="
              (cartId, hash) => emits('removeDraftItem', cartId, hash)
            "
            @delete-draft="emits('deleteDraft', $event)"
            @save-draft="emits('saveDraft', $event)"
          />

          <!--
          Saved cart rows — immutable server-side; copy, copy-to-draft, or delete only.
          openCartId enforces single-open accordion behavior across all rows.
          -->
          <CartSavedRow
            v-for="cart in savedCarts"
            :key="cart.id"
            :open="openCartId === cart.id"
            :cart="cart"
            :compiling-cart-id="compilingCartId"
            @update:open="handleToggleExpand(cart.id, 'saved', $event)"
            @copy="emits('copy', $event)"
            @copy-to-draft="emits('copyToDraft', $event)"
            @delete-saved="emits('deleteSaved', $event)"
          />
        </div>

        <!-- "+ New cart" button — disabled at the combined cart cap. -->
        <div class="flex justify-end">
          <UButton
            label="New cart"
            icon="i-hugeicons-add-square"
            color="neutral"
            variant="subtle"
            size="sm"
            :disabled="totalCartCount >= maxCartsPerOwner"
            @click="emits('newDraft')"
          />
        </div>
      </div>
    </template>
  </USlideover>
</template>

<script setup lang="ts">
import type { CartResponse } from "@/api/generated";
import type { DraftCart } from "@/stores/cart-store";
import CartDraftRow from "./CartDraftRow.vue";
import CartSavedRow from "./CartSavedRow.vue";

const props = defineProps<{
  draftCarts: DraftCart[];
  activeDraftCartId: string | null;
  savedCarts: CartResponse[];
  totalCartCount: number;
  totalItemCount: number;
  compilingCartId: string | null;
  savingCartId: string | null;
  maxCartsPerOwner: number;
  maxItemsPerCart: number;
}>();

const emits = defineEmits<{
  newDraft: [];
  setActive: [cartId: string];
  removeDraftItem: [cartId: string, hash: string];
  deleteDraft: [cartId: string];
  saveDraft: [cartId: string];
  copy: [cartId: string];
  copyToDraft: [cartId: string];
  deleteSaved: [cartId: string];
}>();

const isOpen = ref(false);

/** Tracks which cart's items are currently expanded. */
const openCartId = ref<string | null>(null);

function handleToggleExpand(_cartId: string, _kind: string, open: boolean) {
  openCartId.value = open ? _cartId : null;
}
</script>
