<template>
  <!--
  Collapsible row for a single immutable saved cart.
  Saved carts cannot be edited in place; the only mutations are
  copy-to-draft (creates a new local draft) and delete. The copy
  button copies the short MCP agent instruction; the full findings
  XML is fetched by the agent via get_cart_findings.
  -->
  <CartRowShell
    v-model:open="open"
    :name="cart.name"
    :date-label="formatDate(String(cart.updatedAtUtc))"
    badge-label="Saved"
    :count-label="`${cart.itemCount} finding${+cart.itemCount === 1 ? '' : 's'}`"
  >
    <template #leading>
      <!-- Re-open this cart for editing by creating a new local draft. -->
      <UTooltip text="Copy findings into a new draft">
        <UButton
          icon="i-hugeicons-copy-link"
          color="neutral"
          variant="ghost"
          size="xs"
          square
          aria-label="Copy to new draft"
          @click.stop="emits('copyToDraft', cart.id)"
        />
      </UTooltip>
    </template>

    <template #actions>
      <!-- Copy the short MCP instruction to clipboard. -->
      <UTooltip text="Copy agent instructions to clipboard">
        <UButton
          :icon="
            compilingCartId === cart.id
              ? 'i-hugeicons-checkmark-circle-02'
              : 'i-hugeicons-copy-01'
          "
          :loading="compilingCartId === cart.id"
          color="neutral"
          variant="ghost"
          size="xs"
          square
          aria-label="Copy cart instructions"
          @click.stop="emits('copy', cart.id)"
        />
      </UTooltip>

      <!-- Delete this saved cart from the server. -->
      <UTooltip text="Delete this saved cart">
        <UButton
          icon="i-hugeicons-delete-02"
          color="neutral"
          variant="ghost"
          size="xs"
          square
          aria-label="Delete cart"
          @click.stop="emits('deleteSaved', cart.id)"
        />
      </UTooltip>
    </template>

    <template #content>
      <!-- Read-only finding summary list; no remove action on saved carts. -->
      <CartFindingSummaryList :items="cart.items" />
    </template>
  </CartRowShell>
</template>

<script setup lang="ts">
import type { CartResponse } from "@/api/generated";
import CartFindingSummaryList from "./CartFindingSummaryList.vue";
import CartRowShell from "./CartRowShell.vue";

/** Two-way open/close state driven by the parent (single-open accordion). */
const open = defineModel<boolean>("open", { default: false });

const props = defineProps<{
  cart: CartResponse;
  /** Cart id currently having its text compiled; drives the loading spinner. */
  compilingCartId: string | null;
}>();

const emits = defineEmits<{
  copy: [cartId: string];
  copyToDraft: [cartId: string];
  deleteSaved: [cartId: string];
}>();

function formatDate(isoTimestamp: string): string {
  return new Date(isoTimestamp).toLocaleString();
}
</script>
