<template>
  <ZeeqView id="settings" title="Settings">
    <template #toolbar>
      <UNavigationMenu :items="links" highlight class="-mx-1 flex-1" />
    </template>

    <div
      class="mx-auto flex w-full flex-col gap-4 sm:gap-6 lg:max-w-3xl lg:gap-12"
    >
      <RouterView />
    </div>
  </ZeeqView>
</template>

<script setup lang="ts">
import { computed } from "vue";
import { useRoute } from "vue-router";
import { storeToRefs } from "pinia";
import ZeeqView from "@/components/ZeeqView.vue";
import { useOrganizationSettingsStore } from "@/stores/organization-settings-store";

const route = useRoute();
const { canManageOrganization } = storeToRefs(useOrganizationSettingsStore());
import type { NavigationMenuItem } from "@nuxt/ui";

/**
 * Route-backed settings navigation mirrors the sidebar and matches the
 * dashboard toolbar pattern used by other nested sections.
 */
const links = computed<NavigationMenuItem[][]>(() => [
  [
    {
      label: "Organization",
      icon: "i-hugeicons-building-03",
      to: "/settings/organization",
    },
    {
      label: "Members",
      icon: "i-hugeicons-user-group",
      to: "/settings/members",
    },
    ...(canManageOrganization.value
      ? [
          {
            label: "Teams",
            icon: "i-lucide-users-round",
            to: "/settings/teams",
            active:
              route.path === "/settings/teams" ||
              route.path.startsWith("/settings/teams/"),
          },
        ]
      : []),
    {
      label: "My Memberships",
      icon: "i-hugeicons-id",
      to: "/settings/memberships",
    },
    {
      label: "Me",
      icon: "i-hugeicons-user-account",
      to: "/settings/me",
    },
    {
      label: "Credentials",
      icon: "i-hugeicons-key-01",
      to: "/settings/credentials",
    },
    {
      label: "GitHub",
      icon: "i-hugeicons-github",
      to: "/settings/github",
    },
    {
      label: "LLM Configuration",
      icon: "i-hugeicons-artificial-intelligence-08",
      to: "/settings/llm-config",
    },
  ],
]);
</script>
