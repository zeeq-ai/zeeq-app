<!-- Dense label/value row for the Sessions detail sidebar's metadata and stat lists. -->
<template>
  <div class="flex items-center justify-between gap-3 px-2 py-1 text-xs">
    <span class="text-muted">{{ label }}</span>
    <span class="min-w-0 truncate text-right font-medium text-highlighted">
      {{ value }}
    </span>
  </div>
</template>

<script setup lang="ts">
defineProps<{
  label: string;
  value: string;
}>();
</script>
