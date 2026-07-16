<template>
  <!--
  Slideover for repository-level configuration settings.
  The parent owns the API actions; this component collects user input and calls
  the provided handlers on submit.
  -->
  <USlideover v-model:open="open" side="right" title="Repository settings">
    <template #body>
      <div class="flex flex-col gap-4">
        <p class="text-sm text-muted">
          Configure settings for
          <span class="font-medium text-highlighted">{{
            repository?.displayName ?? repository?.ownerQualifiedName
          }}</span
          >.
        </p>

        <UAccordion
          type="multiple"
          :default-value="['libraries']"
          :items="accordionItems"
          :ui="{
            root: 'border border-default rounded-md',
            trigger: 'px-4 py-3 hover:bg-elevated/40',
            body: 'px-4 pb-4 pt-0',
          }"
        >
          <template #body="{ item }">
            <div v-if="item.value === 'libraries'" class="flex flex-col gap-3">
              <p class="text-xs text-muted">
                Reviewer agents for this repository will have access to the
                selected libraries.
              </p>

              <USeparator />

              <UAlert
                v-if="libraries.length === 0"
                title="No libraries"
                description="Create a library first in the Libraries section, then return here to map it."
                icon="i-hugeicons-book-01"
                color="neutral"
                variant="subtle"
              />

              <UCheckboxGroup
                v-else
                v-model="selectedLibraryIds"
                :items="libraryItems"
                :disabled="submitting"
              />
            </div>

            <div
              v-else-if="item.value === 'check-runs'"
              class="flex flex-col gap-3"
            >
              <p class="text-xs text-muted">
                When enabled, code review findings that meet the selected
                severity thresholds will block the pull request merge. A
                branch-protection ruleset must be imported for the block to take
                effect.
              </p>

              <USeparator />

              <div class="flex flex-col gap-2">
                <UCheckbox
                  v-model="checkRunCritical"
                  label="Block on CRITICAL findings"
                  :disabled="checkRunMajor || submitting"
                />
                <UCheckbox
                  v-model="checkRunMajor"
                  label="Block on MAJOR findings (implies CRITICAL)"
                  :disabled="submitting"
                />
              </div>

              <div
                v-if="checkRunCritical || checkRunMajor"
                class="flex flex-col gap-2 pt-1"
              >
                <p class="text-xs text-muted">
                  Branch-protection ruleset JSON for the
                  <span class="font-mono">Zeeq Code Review</span> check context.
                </p>
                <p class="text-xs text-muted">
                  Import in GitHub: Repository Settings → Rules → Rulesets → New
                  ruleset → Import a ruleset.
                </p>
                <div class="flex items-center gap-2 self-end">
                  <UButton
                    label="Download ruleset JSON"
                    icon="i-hugeicons-download-04"
                    color="neutral"
                    variant="ghost"
                    size="xs"
                    to="/api/v1/assets/zeeq-code-review-ruleset.json"
                    target="_blank"
                  />
                  <UButton
                    label="Open settings"
                    icon="i-hugeicons-link-square-01"
                    color="neutral"
                    variant="ghost"
                    size="xs"
                    :to="`https://github.com/${repository?.ownerQualifiedName}/settings/rules`"
                    target="_blank"
                  />
                </div>
              </div>
            </div>

            <div
              v-else-if="item.value === 'danger'"
              class="flex flex-col gap-3"
            >
              <p class="text-xs text-muted">
                Permanent actions that cannot be undone.
              </p>

              <USeparator />

              <div class="flex items-center justify-between gap-4">
                <div>
                  <p class="text-sm font-medium text-highlighted">
                    Remove repository
                  </p>
                  <p class="text-xs text-muted mt-0.5">
                    Removes the Zeeq mapping. Review history is preserved.
                  </p>
                </div>

                <ZeeqPopConfirm
                  title="Remove repository?"
                  body="This removes the active Zeeq mapping. Existing review history remains, and enabling the repository again creates a new mapping."
                  confirm-label="Remove"
                  label="Remove"
                  icon="i-hugeicons-delete-02"
                  color="error"
                  variant="ghost"
                  size="sm"
                  :disabled="removing"
                  :loading="removing"
                  @confirm="onRemove"
                />
              </div>
            </div>
          </template>
        </UAccordion>
      </div>
    </template>

    <template #footer>
      <div class="flex gap-3 ml-auto">
        <UButton
          label="Cancel"
          color="neutral"
          variant="ghost"
          @click="
            () => {
              open = false;
            }
          "
        />
        <UButton
          label="Save"
          color="neutral"
          variant="subtle"
          :loading="submitting"
          @click="onSubmit"
        />
      </div>
    </template>
  </USlideover>
</template>

<script setup lang="ts">
import ZeeqPopConfirm from "@/components/ZeeqPopConfirm.vue";
import type { LibraryResponse } from "@/api/generated/types/LibraryResponse";
import type { CodeReviewCheckRunConfigurationDto } from "@/api/generated/types/CodeReviewCheckRunConfigurationDto";
import type { GitHubConfiguredRepository } from "@/stores/github-settings-store";

const props = defineProps<{
  repository: GitHubConfiguredRepository | null;
  libraries: LibraryResponse[];
  submitHandler: (
    libraryIds: string[],
    checkRunConfig: CodeReviewCheckRunConfigurationDto | null,
  ) => Promise<void>;
  removeHandler: () => Promise<void>;
}>();

const open = defineModel<boolean>("open", { required: true });

const submitting = ref(false);
const removing = ref(false);
const selectedLibraryIds = ref<string[]>([]);
const checkRunCritical = ref(false);
const checkRunMajor = ref(false);

const accordionItems = [
  { label: "Libraries", value: "libraries", icon: "i-hugeicons-book-01" },
  {
    label: "Check runs",
    value: "check-runs",
    icon: "i-hugeicons-checkmark-badge-01",
  },
  { label: "Danger zone", value: "danger", icon: "i-hugeicons-alert-02" },
];

/** Checkbox items derived from the available libraries. */
const libraryItems = computed(() =>
  props.libraries.map((l) => ({ value: l.id, label: l.name })),
);

/** Seed checkboxes from the repository's current library mapping. */
watch(
  () => props.repository,
  (repo) => {
    selectedLibraryIds.value = repo ? [...repo.libraryIds] : [];
  },
  { immediate: true },
);

/**
 * Two-way binding for the check-run configuration.
 * Major implies Critical so the Critical checkbox is checked and disabled when Major is on.
 */
const checkRunConfig = defineModel<CodeReviewCheckRunConfigurationDto | null>(
  "checkRunConfig",
);

/** Seeds the check-run checkboxes from the bound model. */
watch(
  checkRunConfig,
  (config) => {
    checkRunCritical.value = config?.blockOnCritical ?? false;
    checkRunMajor.value = config?.blockOnMajor ?? false;
  },
  { immediate: true },
);

/** Keeps Major→Critical implication in sync without a cycle. */
watch(checkRunMajor, (major) => {
  if (major) {
    checkRunCritical.value = true;
  }
});

/** Builds the check-run DTO from the local checkbox state. */
function buildCheckRunConfig(): CodeReviewCheckRunConfigurationDto | null {
  if (!checkRunCritical.value && !checkRunMajor.value) {
    return null;
  }
  return {
    blockOnCritical: checkRunCritical.value || checkRunMajor.value,
    blockOnMajor: checkRunMajor.value,
  };
}

async function onSubmit() {
  submitting.value = true;
  try {
    await props.submitHandler(selectedLibraryIds.value, buildCheckRunConfig());
  } finally {
    submitting.value = false;
  }
}

async function onRemove() {
  removing.value = true;
  try {
    await props.removeHandler();
  } finally {
    removing.value = false;
  }
}
</script>
