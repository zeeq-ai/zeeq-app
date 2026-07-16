<template>
  <Suspense>
    <UApp>
      <RouterView />
    </UApp>
  </Suspense>
</template>

<script setup lang="ts">
import { computed, inject, watch, type Ref } from "vue";
import { useHead } from "@unhead/vue";
import { useColorMode } from "@vueuse/core";

const colorMode = useColorMode();
const themeColor = computed(() =>
  colorMode.value === "dark" ? "#18181b" : "#ffffff",
);

useHead({
  meta: [{ name: "theme-color", content: themeColor }],
});

const needRefresh = inject<Ref<boolean>>("pwaNeedRefresh");
const updateServiceWorker = inject<() => Promise<void>>("pwaUpdateServiceWorker");

if (needRefresh && updateServiceWorker) {
  const toast = useToast();

  watch(needRefresh, (val) => {
    if (val) {
      toast.add({
        title: "Update available",
        description: "A new version is ready.",
        duration: 30000,
        color: "info",
        actions: [
          {
            label: "Update",
            color: "primary",
            variant: "solid",
            onClick: () => updateServiceWorker(),
          },
        ],
      });
    }
  });
}
</script>
