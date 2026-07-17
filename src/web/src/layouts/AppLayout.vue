<template>
  <UDashboardGroup
    unit="rem"
    storage="local"
    :storage-key="dashboardStorageKey"
  >
    <UDashboardSidebar
      :id="sidebarId"
      v-model:open="sidebarOpen"
      v-model:collapsed="sidebarCollapsed"
      collapsible
      resizable
      class="bg-elevated/25"
      :ui="{ footer: 'lg:border-t lg:border-default' }"
    >
      <!-- Top section of the sidebar with team/org selection -->
      <template #header="{ collapsed }">
        <OrganizationsMenu :collapsed="collapsed" />
      </template>

      <!-- Middle section with navigation -->
      <template #default="{ collapsed }">
        <UNavigationMenu
          :collapsed="collapsed"
          :items="navigationLinks[0]"
          orientation="vertical"
          tooltip
          popover
        />

        <UNavigationMenu
          :collapsed="collapsed"
          :items="navigationLinks[1]"
          orientation="vertical"
          tooltip
          class="mt-auto"
        />
      </template>

      <!-- Bottom user menu -->
      <template #footer="{ collapsed }">
        <UserMenu :collapsed="collapsed" />
      </template>
    </UDashboardSidebar>

    <RouterView />

    <!-- Global app navigation palette. Mounted only in the authenticated app shell. -->
    <UDashboardSearch
      :groups="commandGroups"
      :color-mode="false"
      placeholder="Search navigation..."
      title="Command palette"
      description="Navigate through Zeeq"
    />
  </UDashboardGroup>
</template>

<script setup lang="ts">
import { computed, ref } from "vue";
import { useStorage } from "@vueuse/core";
import { useAppStore } from "@/stores/app-store";
import {
  buildAppCommandGroups,
  buildAppNavigationLinks,
} from "@/layouts/app-navigation";

type DashboardSidebarStorage = {
  size: number;
  collapsed: boolean;
};

const dashboardStorageKey = "dashboard";
const sidebarId = "default";
const sidebarDefaultStorage: DashboardSidebarStorage = {
  size: 15,
  collapsed: false,
};

const sidebarOpen = ref(false);
const appStore = useAppStore();

// Share Nuxt UI's resizable-sidebar storage object so the collapse toggle and
// stored width stay in sync under the same localStorage key.
const sidebarStorage = useStorage<DashboardSidebarStorage>(
  `${dashboardStorageKey}-sidebar-${sidebarId}`,
  sidebarDefaultStorage,
  undefined,
  { mergeDefaults: true },
);

const sidebarCollapsed = computed({
  get: () => sidebarStorage.value.collapsed,
  set: (collapsed: boolean) => {
    sidebarStorage.value = {
      size: sidebarStorage.value.size,
      collapsed,
    };
  },
});

const navigationLinks = computed(() =>
  buildAppNavigationLinks(appStore.isSystemAdmin, closeSidebar),
);
const commandGroups = computed(() =>
  buildAppCommandGroups(appStore.isSystemAdmin),
);

function closeSidebar() {
  sidebarOpen.value = false;
}
</script>
