<template>
  <ZeeqView id="telemetry">
    <template #toolbar>
      <UNavigationMenu :items="links" highlight class="-mx-1 flex-1" />
    </template>

    <RouterView />
  </ZeeqView>
</template>

<script setup lang="ts">
import type { NavigationMenuItem } from "@nuxt/ui";

const links: NavigationMenuItem[][] = [
  [
    {
      label: "My Conversations",
      to: "/telemetry/my-conversations",
    },
  ],
];
</script>
