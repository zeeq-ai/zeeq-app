<template>
  <!--
  Central configuration panel for one repository. Library mapping and prompt
  customization are tabbed because each has independent save/help actions.
  -->
  <section class="flex min-h-0 flex-1 flex-col overflow-hidden">
    <div
      class="flex min-h-16 items-center gap-3 border-b border-default px-4 py-3 sm:px-6"
    >
      <div class="min-w-0">
        <h2 class="truncate text-base font-semibold text-highlighted">
          {{ repository.ownerQualifiedName }}
        </h2>
        <p class="mt-0.5 truncate text-xs text-muted">
          {{ repository.enabled ? "Reviews enabled" : "Reviews paused" }}
        </p>
      </div>

      <UButton
        label="Open on GitHub"
        icon="i-hugeicons-link-square-01"
        color="neutral"
        variant="ghost"
        size="sm"
        class="ml-auto"
        :to="`https://github.com/${repository.ownerQualifiedName}`"
        target="_blank"
      />
    </div>

    <UTabs
      v-model="activeTab"
      :items="configurationTabs"
      color="neutral"
      variant="link"
      class="min-w-0 flex-1"
      :ui="{
        root: 'min-h-0 flex-1 flex flex-col',
        list: 'shrink-0 px-4 sm:px-6',
        content: 'min-h-0 flex-1 pt-0',
      }"
    >
      <template #libraries>
        <div class="min-h-0 flex-1 overflow-y-auto p-4 sm:px-6">
          <!--
            Library mapping. Reviewer agents may query the selected libraries
            when reviewing pull requests on this repository.
            -->
          <div class="flex flex-col gap-3">
            <div class="flex items-start justify-between gap-3">
              <div>
                <h3 class="text-sm font-semibold text-highlighted">
                  Code review reference libraries
                </h3>
                <p class="mt-1 text-xs text-muted">
                  Select the libraries that code review agents can consult when
                  reviewing pull requests for this repository. If no libraries
                  are selected, the agent does not consult reference material.
                </p>
              </div>

              <UPopover
                mode="hover"
                enable-touch
                :open-delay="300"
                :close-delay="150"
                :content="{ side: 'left', align: 'start' }"
                :ui="{ content: 'w-96' }"
              >
                <UButton
                  label="Help"
                  icon="i-hugeicons-help-circle"
                  color="neutral"
                  variant="ghost"
                  size="sm"
                />
                <template #content>
                  <div class="grid gap-2 p-3 text-sm text-muted">
                    <p class="font-medium text-highlighted">Library mapping</p>
                    <p>
                      Mapped libraries become the repository's default knowledge
                      base for reviewer agents. Agents can query only the
                      libraries selected here when reviewing this repository.
                    </p>
                    <p>
                      This page is member-editable and intentionally does not
                      control GitHub App connection, enable, pause, or removal.
                      Those remain in GitHub settings for organization owners
                      and admins.
                    </p>
                    <p>
                      Paused repositories can still be configured here, so
                      mappings are ready if reviews are re-enabled later.
                    </p>
                  </div>
                </template>
              </UPopover>
            </div>

            <USeparator />

            <UAlert
              v-if="libraries.length === 0"
              title="No libraries"
              description="Create a library first, then return here to map it to this repository."
              icon="i-hugeicons-book-01"
              color="neutral"
              variant="subtle"
            />

            <template v-else>
              <UCheckboxGroup
                v-model="selectedLibraryIds"
                :items="libraryItems"
                :disabled="savingLibraries"
              />

              <div class="flex justify-end">
                <UButton
                  label="Save libraries"
                  color="neutral"
                  variant="subtle"
                  :loading="savingLibraries"
                  :disabled="!librariesDirty"
                  @click="emits('save-libraries', selectedLibraryIds)"
                />
              </div>
            </template>
          </div>
        </div>
      </template>

      <template #prompts>
        <div class="min-h-0 flex-1 overflow-y-auto p-4 sm:px-6">
          <!--
            Repository-scoped MCP prompt customization. Values set here are
            substituted when an agent sends the x-zeeq-prompts-repo header.
            -->
          <div class="flex flex-col gap-3">
            <div class="flex items-start justify-between gap-3">
              <div>
                <h3 class="text-sm font-semibold text-highlighted">
                  Configure repository dynamic skills (MCP prompts)
                </h3>
                <p class="mt-1 text-xs text-muted">
                  Set repository-specific values for dynamic skills which have
                  placeholders. This allows reusing the same skill across
                  multiple repositories with repository specific rules and
                  behaviors. The MCP configuration must send the header:
                  <span class="font-mono text-[11px]">
                    x-zeeq-prompts-repo =
                    {{ repository.ownerQualifiedName }} </span
                  >.
                </p>
              </div>

              <UPopover
                mode="hover"
                enable-touch
                :open-delay="300"
                :close-delay="150"
                :content="{ side: 'left', align: 'start' }"
                :ui="{ content: 'w-[34rem]' }"
              >
                <UButton
                  label="Help"
                  icon="i-hugeicons-help-circle"
                  color="neutral"
                  variant="ghost"
                  size="sm"
                />
                <template #content>
                  <div class="grid gap-3 p-3 text-sm text-muted">
                    <p class="font-medium text-highlighted">
                      MCP prompt customization
                    </p>
                    <p>
                      Agents opt into these repository prompt values by sending
                      the
                      <span class="font-mono text-[12px]">
                        x-zeeq-prompts-repo
                      </span>
                      header. Repository override state does not control whether
                      the prompt is listed or delivered; inactive overrides are
                      still delivered, but Zeeq renders each placeholder's
                      authored default instead of templating saved values.
                    </p>
                    <p>
                      Placeholder values are repository-specific. Leaving a
                      value absent means use the authored default; saving an
                      empty string means render nothing intentionally.
                    </p>

                    <section class="grid min-w-0 gap-2 overflow-x-auto">
                      <h4 class="text-xs font-semibold text-highlighted">
                        Placeholder format
                      </h4>
                      <Comark
                        :markdown="toFencedMarkdown(promptFormatExample, 'xml')"
                        :plugins="codeHighlightPlugins"
                        class="max-w-full text-xs"
                      />
                    </section>
                  </div>
                </template>
              </UPopover>
            </div>

            <RepositoryPromptsPanel
              :organization-id="organizationId"
              :repository-id="repository.id"
              :prompts
              :details
              :loading="loadingPrompts"
              :loading-detail-id="loadingPromptDetailId"
              :saving-id="savingPromptId"
              @expand="
                (documentId, libraryId) =>
                  emits('expand-prompt', documentId, libraryId)
              "
              @save="
                (documentId, libraryId, active, values) =>
                  emits('save-prompt', documentId, libraryId, active, values)
              "
            />
          </div>
        </div>
      </template>
    </UTabs>
  </section>
</template>

<script setup lang="ts">
import { Comark } from "@comark/vue";
import type { TabsItem } from "@nuxt/ui";
import type { LibraryResponse } from "@/api/generated/types/LibraryResponse";
import type { RepositoryPromptSummaryResponse } from "@/api/generated/types/RepositoryPromptSummaryResponse";
import type { RepositoryPromptDetailResponse } from "@/api/generated/types/RepositoryPromptDetailResponse";
import type { GitHubConfiguredRepository } from "@/stores/github-settings-store";
import { useCodeHighlight } from "@/composables/useCodeHighlight";

import RepositoryPromptsPanel from "./RepositoryPromptsPanel.vue";

const props = defineProps<{
  repository: GitHubConfiguredRepository;
  organizationId: string | null;
  libraries: LibraryResponse[];
  prompts: RepositoryPromptSummaryResponse[];
  details: Record<string, RepositoryPromptDetailResponse>;
  loadingPrompts: boolean;
  loadingPromptDetailId: string | null;
  savingPromptId: string | null;
  savingLibraries: boolean;
}>();

const emits = defineEmits<{
  "save-libraries": [libraryIds: string[]];
  "expand-prompt": [documentId: string, libraryId: string];
  "save-prompt": [
    documentId: string,
    libraryId: string,
    active: boolean,
    values: Record<string, string>,
  ];
}>();

const selectedLibraryIds = ref<string[]>([]);
const activeTab = ref("libraries");

const { codeHighlightPlugins, toFencedMarkdown } = useCodeHighlight();

const configurationTabs: TabsItem[] = [
  {
    label: "Libraries",
    value: "libraries",
    slot: "libraries",
    icon: "i-hugeicons-book-01",
  },
  {
    label: "Dynamic skills",
    value: "prompts",
    slot: "prompts",
    icon: "i-hugeicons-ai-file",
  },
];

const promptFormatExample = `<zeeq_placeholder
  label="Review focus"
  description="Review guidance for agents."
>
Prioritize correctness, security, and migration safety.
</zeeq_placeholder>`;

/** Checkbox options derived from the organization's library catalog. */
const libraryItems = computed(() =>
  props.libraries.map((library) => ({
    value: library.id,
    label: library.name,
  })),
);

/** Gates the save button so an untouched mapping cannot be re-submitted. */
const librariesDirty = computed(() => {
  const saved = [...props.repository.libraryIds].sort();
  const draft = [...selectedLibraryIds.value].sort();

  return (
    saved.length !== draft.length || saved.some((id, i) => id !== draft[i])
  );
});

/**
 * Reseeds the checkbox draft whenever the selected repository changes or its
 * saved mapping is refreshed after a successful save.
 */
watch(
  () => props.repository,
  (repository) => {
    selectedLibraryIds.value = [...repository.libraryIds];
  },
  { immediate: true },
);
</script>
