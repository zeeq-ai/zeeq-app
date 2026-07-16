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
          :items="links[0]"
          orientation="vertical"
          tooltip
          popover
        />

        <UNavigationMenu
          :collapsed="collapsed"
          :items="links[1]"
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
  </UDashboardGroup>
</template>

<script setup lang="ts">
import { computed, ref } from "vue";
import type { NavigationMenuItem } from "@nuxt/ui";
import { useStorage } from "@vueuse/core";
import { useAppStore } from "@/stores/app-store";

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

const links = computed<NavigationMenuItem[][]>(() => {
  const primaryLinks: NavigationMenuItem[] = [
    {
      label: "Home",
      icon: "i-hugeicons-home-01",
      to: "/",
      exact: true,
      onSelect: () => {
        sidebarOpen.value = false;
      },
    },
    {
      label: "Libraries",
      icon: "i-hugeicons-hierarchy-files",
      to: "/libraries",
      onSelect: () => {
        sidebarOpen.value = false;
      },
    },
    /*
    {
      label: "Memories",
      icon: "i-hugeicons-chart-relationship",
      to: "/memories",
      onSelect: () => {
        sidebarOpen.value = false;
      },
    },
    */
    {
      label: "Code Reviews",
      icon: "i-hugeicons-message-programming",
      defaultOpen: true,
      type: "trigger",
      children: [
        {
          label: "PR Code Reviews",
          to: "/code-reviews/pull-requests",
          onSelect: () => {
            sidebarOpen.value = false;
          },
        },
        {
          label: "Manage Agents",
          to: "/code-reviews/manage-agents",
          onSelect: () => {
            sidebarOpen.value = false;
          },
        },
      ],
    },
    /*
    {
      label: "Telemetry",
      icon: "i-hugeicons-chat-spark-01",
      defaultOpen: true,
      type: "trigger",
      children: [
        {
          label: "My Conversations",
          to: "/telemetry/my-conversations",
          onSelect: () => {
            sidebarOpen.value = false;
          },
        },
      ],
    },
    */
  ];

  primaryLinks.push({
    label: "Settings",
    icon: "i-hugeicons-settings-01",
    defaultOpen: false,
    type: "trigger",
    children: [
      {
        label: "Organization",
        to: "/settings/organization",
        onSelect: () => {
          sidebarOpen.value = false;
        },
      },
      {
        label: "Members",
        to: "/settings/members",
        onSelect: () => {
          sidebarOpen.value = false;
        },
      },
      {
        label: "My Memberships",
        to: "/settings/memberships",
        onSelect: () => {
          sidebarOpen.value = false;
        },
      },
      {
        label: "Credentials",
        to: "/settings/credentials",
        onSelect: () => {
          sidebarOpen.value = false;
        },
      },
      {
        label: "GitHub",
        to: "/settings/github",
        onSelect: () => {
          sidebarOpen.value = false;
        },
      },
      {
        label: "LLM Configuration",
        to: "/settings/llm-config",
        onSelect: () => {
          sidebarOpen.value = false;
        },
      },
    ],
  });

  if (appStore.isSystemAdmin) {
    primaryLinks.push({
      label: "System",
      icon: "i-hugeicons-shield-01",
      defaultOpen: false,
      type: "trigger",
      children: [
        {
          label: "Diagnostics",
          icon: "i-hugeicons-activity-02",
          to: "/system/diagnostics",
          onSelect: () => {
            sidebarOpen.value = false;
          },
        },
      ],
    });
  }

  return [primaryLinks, []];
});
</script>
