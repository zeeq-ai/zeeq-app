<template>
  <!--
  Agent source library (create mode): a right-side slideover offering two ways
  to seed a new reviewer agent — built-in Zeeq templates or an existing agent
  cloned from any configured repository in the org. The larger surface gives
  room to show each source's description and prompt preview before cloning.

  Store-free: templates, repository options, and per-repo agents arrive as props;
  data-load requests and the final seeded form bubble up via emits. Selecting a
  source converts it to an editable CodeReviewerAgentForm (deep-cloned) so the
  parent can drop it straight into the draft.
  -->
  <USlideover
    v-model:open="open"
    side="right"
    title="Start from a template or existing agent"
    :ui="{ content: 'max-w-xl' }"
  >
    <template #body>
      <UTabs
        v-model="activeTab"
        :items="tabItems"
        color="neutral"
        variant="link"
        :ui="{ root: 'min-h-0', content: 'pt-4' }"
      >
        <!-- Built-in Zeeq personas; curated starting points. -->
        <template #templates>
          <div class="grid gap-3">
            <div v-if="templatesLoading" class="grid gap-3">
              <USkeleton
                v-for="index in 2"
                :key="index"
                class="h-28 rounded-md"
              />
            </div>

            <UEmpty
              v-else-if="templateCards.length === 0"
              icon="i-hugeicons-user-ai"
              title="No templates available"
              description="Built-in reviewer templates could not be loaded."
              variant="naked"
            />

            <!-- Selectable template cards; click to seed the draft form. -->
            <button
              v-for="card in templateCards"
              :key="card.key"
              type="button"
              class="grid cursor-pointer gap-2 rounded-md border border-default p-4 text-left transition-colors hover:border-primary hover:bg-primary/5"
              @click="onSelectTemplate(card.source)"
            >
              <div class="flex items-start justify-between gap-3">
                <h3 class="text-sm font-semibold text-highlighted">
                  {{ card.displayName }}
                </h3>
                <div class="flex shrink-0 items-center gap-1.5">
                  <UBadge
                    :label="card.reviewFacet"
                    color="neutral"
                    variant="outline"
                    size="sm"
                    class="rounded-full"
                  />
                  <UBadge
                    :label="`${card.modelTier} tier`"
                    color="primary"
                    variant="subtle"
                    size="sm"
                    class="rounded-full"
                  />
                </div>
              </div>

              <p class="text-sm text-muted">{{ card.description }}</p>
            </button>
          </div>
        </template>

        <!-- Clone an existing agent from any configured repository in the org. -->
        <template #repositories>
          <div class="grid gap-3">
            <USelect
              v-model="repositoryId"
              :items="repositoryOptions"
              placeholder="Select a repository"
              icon="i-hugeicons-github"
              color="neutral"
              class="w-full"
            />

            <div v-if="repoAgentsLoading" class="grid gap-3">
              <USkeleton
                v-for="index in 3"
                :key="index"
                class="h-24 rounded-md"
              />
            </div>

            <UEmpty
              v-else-if="!repositoryId"
              icon="i-hugeicons-github"
              title="Pick a repository"
              description="Choose a repository to browse its reviewer agents."
              variant="naked"
            />

            <UEmpty
              v-else-if="repoAgentCards.length === 0"
              icon="i-hugeicons-user-ai"
              title="No reviewer agents"
              description="This repository has no configured reviewer agents to copy."
              variant="naked"
            />

            <!-- Selectable agent cards for the chosen repository. -->
            <button
              v-for="card in repoAgentCards"
              :key="card.id"
              type="button"
              class="grid cursor-pointer gap-2 rounded-md border border-default p-4 text-left transition-colors hover:border-primary hover:bg-primary/5"
              @click="onSelectAgent(card.source)"
            >
              <div class="flex items-start justify-between gap-3">
                <h3 class="text-sm font-semibold text-highlighted">
                  {{ card.displayName }}
                </h3>
                <div class="flex shrink-0 items-center gap-1.5">
                  <UBadge
                    :label="card.reviewFacet"
                    color="neutral"
                    variant="outline"
                    size="sm"
                    class="rounded-full"
                  />
                  <UBadge
                    :label="`${card.modelTier} tier`"
                    color="primary"
                    variant="subtle"
                    size="sm"
                    class="rounded-full"
                  />
                </div>
              </div>
            </button>
          </div>
        </template>
      </UTabs>
    </template>
  </USlideover>
</template>

<script setup lang="ts">
import { computed, ref, watch } from "vue";
import type {
  CodeReviewerAgentDto,
  CodeReviewerAgentTemplateDto,
} from "@/api/generated";
import {
  agentToForm,
  templateToForm,
  type CodeReviewerAgentForm,
} from "@/stores/code-review-store";

const open = defineModel<boolean>("open", { required: true });

const props = defineProps<{
  templates: CodeReviewerAgentTemplateDto[];
  templatesLoading: boolean;
  repositoryOptions: { label: string; value: string }[];
  repoAgents: CodeReviewerAgentDto[];
  repoAgentsLoading: boolean;
}>();

const emits = defineEmits<{
  requestTemplates: [];
  requestRepoAgents: [repositoryId: string];
  select: [form: CodeReviewerAgentForm];
}>();

const activeTab = ref<"templates" | "repositories">("templates");
const repositoryId = ref<string | undefined>(undefined);

const tabItems = [
  { label: "Templates", value: "templates", slot: "templates" as const },
  {
    label: "Repositories",
    value: "repositories",
    slot: "repositories" as const,
  },
];

/** Template rows projected into the card shape used by the template tab. */
const templateCards = computed(() =>
  props.templates.map((template) => ({
    key: template.key,
    displayName: template.displayName,
    reviewFacet: template.reviewFacet,
    modelTier: template.modelTier,
    description: template.description,
    // Carry the source DTO so selection maps directly without a re-lookup.
    source: template,
  })),
);

/** Agent rows projected into the card shape used by the repositories tab. */
const repoAgentCards = computed(() =>
  props.repoAgents.map((agent) => ({
    id: agent.id,
    displayName: agent.displayName,
    reviewFacet: agent.reviewFacet,
    modelTier: agent.modelTier,
    // Carry the source DTO so selection maps directly without a re-lookup.
    source: agent,
  })),
);

/** Loads templates once the library opens so the default tab has content. */
watch(open, (isOpen) => {
  if (isOpen) {
    activeTab.value = "templates";
    repositoryId.value = undefined;
    emits("requestTemplates");
  }
});

/** Requests the chosen repository's agents when the user picks a source repo. */
watch(repositoryId, (id) => {
  if (id) {
    emits("requestRepoAgents", id);
  }
});

/**
 * Seeds the draft from a built-in template and closes the library.
 *
 * The card carries its source DTO, so the selected template is cloned directly
 * without re-querying props — this keeps the flow robust if the catalog is later
 * refreshed asynchronously while the library stays open (e.g. generate-on-the-fly).
 * @param template - Source template carried by the selected card.
 */
function onSelectTemplate(template: CodeReviewerAgentTemplateDto) {
  emits("select", templateToForm(template));
  open.value = false;
}

/**
 * Seeds the draft by cloning an existing repository agent and closes the library.
 *
 * The card carries its source DTO, so the selected agent is cloned directly
 * without re-querying props — robust against async list refreshes while open.
 * @param agent - Source agent carried by the selected card.
 */
function onSelectAgent(agent: CodeReviewerAgentDto) {
  emits("select", agentToForm(agent));
  open.value = false;
}
</script>
