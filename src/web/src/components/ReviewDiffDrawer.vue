<template>
  <USlideover
    v-model:open="open"
    side="bottom"
    :ui="{ content: 'h-[85vh] overflow-hidden' }"
  >
    <template #content>
      <UCard
        class="h-full w-full"
        variant="soft"
        :ui="{
          root: 'h-full w-full flex flex-col',
          header: 'flex items-center justify-between gap-3',
          body: 'flex flex-1 items-start min-h-0 overflow-hidden p-4 sm:p-4',
        }"
      >
        <template #header>
          <h2 class="font-semibold text-highlighted">{{ title }}</h2>
          <div class="flex items-center gap-3">
            <div class="flex items-center gap-3 text-xs text-muted">
              <span class="flex items-center gap-1">
                <UKbd value="escape" size="sm" />
                to cancel
              </span>
              <span class="flex items-center gap-1">
                <UKbd value="meta" size="sm" /><UKbd size="sm">S</UKbd>
                to save
              </span>
            </div>
            <UButton
              label="Cancel"
              color="neutral"
              variant="ghost"
              @click="closeDrawer"
            />
            <UButton
              :label="confirmLabel"
              icon="i-hugeicons-checkmark-circle-02"
              color="primary"
              variant="subtle"
              :loading="saving"
              @click="confirmSave"
            />
          </div>
        </template>

        <CodeDiff
          :old-string="original"
          :new-string="next"
          output-format="side-by-side"
          :theme="diffTheme"
          class="review-diff-viewer min-h-0"
        />
      </UCard>
    </template>
  </USlideover>
</template>

<script setup lang="ts">
/**
 * Shared bottom diff drawer for reviewing text changes before save.
 * The caller owns persistence; this component only displays the diff and confirms intent.
 */
import { CodeDiff as RawCodeDiff } from "v-code-diff";
import { h, defineComponent } from "vue";

const CodeDiff = defineComponent<{
  oldString: string;
  newString: string;
  outputFormat: "side-by-side" | "line-by-line";
  theme: "light" | "dark";
  class?: string;
}>(
  (props) => () =>
    h(RawCodeDiff, {
      "old-string": props.oldString,
      "new-string": props.newString,
      "output-format": props.outputFormat,
      theme: props.theme,
      class: props.class,
    }),
  {
    props: ["oldString", "newString", "outputFormat", "theme", "class"],
  },
);

withDefaults(
  defineProps<{
    original: string;
    next: string;
    title?: string;
    confirmLabel?: string;
  }>(),
  {
    title: "Review changes",
    confirmLabel: "Save changes",
  },
);

const open = defineModel<boolean>("open", { required: true });

const emits = defineEmits<{
  confirm: [];
}>();

const saving = ref(false);
const colorMode = useColorMode();
const diffTheme = computed<"light" | "dark">(() =>
  colorMode.value === "dark" ? "dark" : "light",
);

watch(open, (isOpen) => {
  if (!isOpen) {
    saving.value = false;
  }
});

function closeDrawer() {
  open.value = false;
}

function confirmSave() {
  saving.value = true;
  emits("confirm");
}

defineExpose({
  triggerSave: confirmSave,
});
</script>

<style scoped>
:deep(.review-diff-viewer.code-diff-view) {
  max-height: 100%;
  margin: 0;
  overflow: auto;
  width: 100%;
}
</style>
