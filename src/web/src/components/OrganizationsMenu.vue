<script setup lang="ts">
import { computed } from "vue";
import type { DropdownMenuItem } from "@nuxt/ui";
import { storeToRefs } from "pinia";
import { useAppStore } from "@/stores/app-store";
import { isActivatedOrganization } from "@/utils/organizationAccess";

defineProps<{
  collapsed?: boolean;
}>();

const store = useAppStore();
const { user: me, currentOrganization } = storeToRefs(store);

/**
 * /me is the tenancy source for the shell. Pending invitations and inactive
 * organizations are excluded because selecting them would produce unusable
 * tenant claims.
 */
const activeOrganizations = computed(
  () => me.value?.organizations?.filter(isActivatedOrganization) ?? [],
);

const selectedOrganization = computed(() => {
  return currentOrganization.value ?? activeOrganizations.value[0] ?? null;
});

const items = computed<DropdownMenuItem[][]>(() => {
  return [
    activeOrganizations.value.map((org) => ({
      label: org.displayName,
      avatar: {
        src: org.iconUrl ?? undefined,
        alt: "",
        icon: "i-hugeicons-cube",
      },
      type: "checkbox" as const,
      checked: org.id === selectedOrganization.value?.id,
      onSelect: async () => {
        if (org.id !== selectedOrganization.value?.id) {
          await store.switchOrganization(org.id);
        }
      },
    })),
    [
      {
        label: "Manage organization",
        icon: "i-hugeicons-settings-02",
        to: "/settings/organization",
      },
    ],
  ];
});
</script>

<template>
  <UDropdownMenu
    :items="items"
    :content="{ align: 'center', collisionPadding: 12 }"
    :ui="{
      content: collapsed ? 'w-40' : 'w-(--reka-dropdown-menu-trigger-width)',
    }"
  >
    <UButton
      v-bind="{
        label: collapsed
          ? undefined
          : (selectedOrganization?.displayName ?? 'No organization'),
        avatar: {
          src: selectedOrganization?.iconUrl ?? undefined,
          alt: collapsed
            ? (selectedOrganization?.displayName ?? 'No organization')
            : '',
          icon: 'i-hugeicons-cube',
        },
        trailingIcon: collapsed ? undefined : 'i-hugeicons-arrow-up-down',
      }"
      color="neutral"
      variant="ghost"
      block
      :square="collapsed"
      class="data-[state=open]:bg-elevated"
      :class="[!collapsed && 'py-2']"
      :ui="{
        trailingIcon: 'text-dimmed',
      }"
    />
  </UDropdownMenu>
</template>
