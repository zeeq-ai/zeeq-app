<template>
  <!--
  Bottom diff drawer (D-6): opens from the bottom at ~85vh for the widest
  side-by-side review surface. Wraps v-code-diff <CodeDiff>.
  -->
  <USlideover
    v-model:open="open"
    side="bottom"
    :ui="{
      content: 'h-[85vh] overflow-hidden',
    }"
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
          <h2 class="text-highlighted font-semibold">Review changes</h2>
          <div class="flex items-center gap-2">
            <UButton
              label="Cancel"
              color="neutral"
              variant="outline"
              @click="closeDrawer"
            />
            <UButton
              label="Save changes"
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
          class="library-diff-viewer min-h-0"
        />
      </UCard>
    </template>
  </USlideover>
</template>

<script setup lang="ts">
/**
 * Typed wrapper around v-code-diff's CodeDiff component.
 * v-code-diff's types declare empty props (DefineComponent<{}, {}, any>),
 * so we re-declare with the actual runtime props to satisfy the type checker.
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

defineProps<{
  /** Original markdown before edits. */
  original: string;
  /** Next markdown after edits. */
  next: string;
}>();

const open = defineModel<boolean>("open", { required: true });

const emits = defineEmits<{
  confirm: [];
}>();

const saving = ref(false);

/** Resets the saving state when the drawer closes, regardless of outcome. */
watch(open, (isOpen) => {
  if (!isOpen) {
    saving.value = false;
  }
});

/** Diff theme follows the app color mode for consistency. */
const colorMode = useColorMode();
const diffTheme = computed<"light" | "dark">(() =>
  colorMode.value === "dark" ? "dark" : "light",
);

function closeDrawer() {
  open.value = false;
}

function confirmSave() {
  saving.value = true;
  emits("confirm");
}
</script>

<style scoped>
:deep(.library-diff-viewer.code-diff-view) {
  max-height: 100%;
  margin: 0;
  overflow: auto;
  width: 100%;
}
</style>
