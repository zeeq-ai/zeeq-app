<template>
  <ZeeqView id="system" title="System">
    <template #toolbar>
      <UNavigationMenu :items="links" highlight class="-mx-1 flex-1" />
    </template>

    <div class="mx-auto flex w-full flex-col gap-4 lg:max-w-3xl">
      <RouterView />
    </div>
  </ZeeqView>
</template>

<script setup lang="ts">
import ZeeqView from "@/components/ZeeqView.vue";
import type { NavigationMenuItem } from "@nuxt/ui";

/** System navigation is route-backed so admin pages remain shareable URLs. */
const links: NavigationMenuItem[][] = [
  [
    {
      label: "Diagnostics",
      icon: "i-hugeicons-activity-02",
      to: "/system/diagnostics",
    },
  ],
];
</script>
