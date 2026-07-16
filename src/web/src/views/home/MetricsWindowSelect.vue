<template>
  <!--
  Shared window selector rendered outside the tabs so switching the range
  reshapes every panel at once. Two-way bound to the store's `window` at the
  Home root via defineModel.
  -->
  <USelect
    v-model="selectedWindow"
    :items="metricWindowItems"
    icon="i-hugeicons-clock-01"
    class="w-48"
  />
</template>

<script setup lang="ts">
import {
  metricWindowItems,
  type MetricWindowToken,
} from "@/stores/metrics-store";

// Two-way bound window token; Home maps this onto the metrics store.
const selectedWindow = defineModel<MetricWindowToken>({ required: true });
</script>
