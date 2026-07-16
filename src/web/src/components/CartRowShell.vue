<template>
  <!-- Shared collapsible frame for draft and saved cart rows. -->
  <UCollapsible :open="open" @update:open="open = $event">
    <template #default>
      <div class="flex items-center gap-2 rounded-md border border-default p-2">
        <slot name="leading" />

        <!-- Cart identity: generated name plus the relevant draft/save timestamp. -->
        <div class="min-w-0 flex-1">
          <p class="truncate font-mono text-sm text-highlighted">
            {{ name }}
          </p>
          <p class="text-xs text-muted">{{ dateLabel }}</p>
        </div>

        <UBadge :label="badgeLabel" color="neutral" variant="subtle" />
        <span class="text-xs text-muted">{{ countLabel }}</span>

        <slot name="actions" />

        <UButton
          class="group"
          color="neutral"
          variant="ghost"
          trailing-icon="i-hugeicons-arrow-down-01"
          :ui="{
            trailingIcon:
              'group-data-[state=open]:rotate-180 transition-transform duration-200',
          }"
        />
      </div>
    </template>

    <template #content>
      <slot name="content" />
    </template>
  </UCollapsible>
</template>

<script setup lang="ts">
/** Two-way open/close state driven by the parent single-open accordion. */
const open = defineModel<boolean>("open", { default: false });

defineProps<{
  name: string;
  dateLabel: string;
  badgeLabel: string;
  countLabel: string;
}>();
</script>
