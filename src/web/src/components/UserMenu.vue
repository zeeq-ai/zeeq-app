<template>
  <UDropdownMenu
    :items="items"
    :content="{ align: 'center', collisionPadding: 12 }"
    :ui="{
      content: collapsed ? 'w-48' : 'w-(--reka-dropdown-menu-trigger-width)',
    }"
  >
    <UButton
      v-bind="{
        ...displayUser,
        label: collapsed ? undefined : displayUser?.name,
        trailingIcon: collapsed ? undefined : 'i-hugeicons-arrow-up-down',
      }"
      color="neutral"
      variant="ghost"
      block
      :square="collapsed"
      class="data-[state=open]:bg-elevated"
      :ui="{
        trailingIcon: 'text-dimmed',
      }"
    />

    <template #chip-leading="{ item }">
      <div class="inline-flex items-center justify-center shrink-0 size-5">
        <span
          class="rounded-full ring ring-bg bg-(--chip-light) dark:bg-(--chip-dark) size-2"
          :style="{
            '--chip-light': `var(--color-${(item as any).chip}-500)`,
            '--chip-dark': `var(--color-${(item as any).chip}-400)`,
          }"
        />
      </div>
    </template>

  </UDropdownMenu>
</template>

<script setup lang="ts">
import { computed, onMounted } from "vue";
import type { DropdownMenuItem } from "@nuxt/ui";
import { useColorMode } from "@vueuse/core";
import { useAppStore } from "@/stores/app-store";
import { storeToRefs } from "pinia";
import { useRouter } from "vue-router";

defineProps<{
  collapsed?: boolean;
}>();

const colorMode = useColorMode();
const appConfig = useAppConfig();
const store = useAppStore();
const router = useRouter();
const { backendVersion, user: me } = storeToRefs(store);

const colors = [
  "red",
  "orange",
  "amber",
  "yellow",
  "lime",
  "green",
  "emerald",
  "teal",
  "cyan",
  "sky",
  "blue",
  "indigo",
  "violet",
  "purple",
  "fuchsia",
  "pink",
  "rose",
];
const neutrals = ["slate", "gray", "zinc", "neutral", "stone"];

const displayUser = computed(() => ({
  name: me.value?.name || "Unknown User",
  avatar: me.value?.pictureUrl
    ? {
        src: me.value.pictureUrl,
        alt: me.value.name || "",
      }
    : me.value?.email
      ? {
          src: `https://www.gravatar.com/avatar/${me.value.email}?d=mp`,
          alt: me.value.name || "",
        }
      : undefined,
}));
const backendVersionLabel = computed(() => {
  if (!backendVersion.value) {
    return "Version unavailable";
  }

  return `${backendVersion.value.displayVersion} · ${shortSha(backendVersion.value.sha)}`;
});
const backendVersionDateLabel = computed(() => {
  if (!backendVersion.value) {
    return "Build date unavailable";
  }

  return backendVersion.value.buildTimeEst
    ? backendVersion.value.buildTimeEst
    : backendVersion.value.checkedAtUtc;
});

const items = computed<DropdownMenuItem[][]>(() => [
  [
    {
      type: "label",
      label: displayUser.value.name,
      avatar: displayUser.value.avatar,
    },
  ],
  [
    // {
    //   label: "Profile",
    //   icon: "i-hugeicons-user",
    // },
    // {
    //   label: "Billing",
    //   icon: "i-hugeicons-credit-card",
    // },
    {
      label: "Settings",
      icon: "i-hugeicons-settings-01",
      to: "/settings",
    },
  ],
  [
    {
      label: "Theme",
      icon: "i-hugeicons-paint-board",
      children: [
        {
          label: "Primary",
          slot: "chip",
          chip: appConfig.ui.colors.primary,
          content: {
            align: "center",
            collisionPadding: 16,
          },
          children: colors.map((color) => ({
            label: color,
            chip: color,
            slot: "chip",
            checked: appConfig.ui.colors.primary === color,
            type: "checkbox",
            onSelect: (e) => {
              e.preventDefault();

              appConfig.ui.colors.primary = color;
            },
          })),
        },
        {
          label: "Neutral",
          slot: "chip",
          chip:
            appConfig.ui.colors.neutral === "neutral"
              ? "old-neutral"
              : appConfig.ui.colors.neutral,
          content: {
            align: "end",
            collisionPadding: 16,
          },
          children: neutrals.map((color) => ({
            label: color,
            chip: color === "neutral" ? "old-neutral" : color,
            slot: "chip",
            type: "checkbox",
            checked: appConfig.ui.colors.neutral === color,
            onSelect: (e) => {
              e.preventDefault();

              appConfig.ui.colors.neutral = color;
            },
          })),
        },
      ],
    },
    {
      label: "Appearance",
      icon: "i-hugeicons-sun-03",
      children: [
        {
          label: "Light",
          icon: "i-hugeicons-sun-03",
          type: "checkbox",
          checked: colorMode.value === "light",
          onSelect(e: Event) {
            e.preventDefault();

            colorMode.value = "light";
          },
        },
        {
          label: "Dark",
          icon: "i-hugeicons-moon-02",
          type: "checkbox",
          checked: colorMode.value === "dark",
          onUpdateChecked(checked: boolean) {
            if (checked) {
              colorMode.value = "dark";
            }
          },
          onSelect(e: Event) {
            e.preventDefault();
          },
        },
      ],
    },
  ],
  /*
  [
    {
      label: "Documentation",
      icon: "i-hugeicons-book-open-01",
      to: "https://ui.nuxt.com/docs/getting-started/installation/vue",
      target: "_blank",
    },
    {
      label: "GitHub repository",
      icon: "simple-icons:github",
      to: "https://github.com/nuxt-ui-templates/dashboard-vue",
      target: "_blank",
    },
  ],
  */
  [
    {
      label: backendVersionLabel.value,
      icon: "i-hugeicons-code-circle",
      onSelect(e: Event) {
        e.preventDefault();
      },
    },
    {
      label: backendVersionDateLabel.value,
      icon: "i-hugeicons-calendar-03",
      onSelect(e: Event) {
        e.preventDefault();
      },
    },
  ],
  [
    {
      label: "Log out",
      icon: "i-hugeicons-logout-01",
      onSelect: async () => {
        await store.logout();
        await router.push("/login");
      },
    },
  ],
]);

onMounted(() => {
  void store.fetchBackendVersion();
});

function shortSha(sha?: string | null) {
  return sha?.slice(0, 8) ?? "unknown";
}
</script>
