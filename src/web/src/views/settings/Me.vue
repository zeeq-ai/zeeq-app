<template>
  <div class="grid gap-4">
    <UPageCard
      title="Me"
      :description="`Organization-scoped aliases for: ${organizationName}.`"
      variant="naked"
      orientation="horizontal"
      class="mb-2"
    >
      <div class="flex w-fit gap-2 lg:ms-auto">
        <UButton
          label="Save aliases"
          icon="i-hugeicons-floppy-disk"
          color="neutral"
          variant="subtle"
          :loading="savingAliases"
          :disabled="!isDirty || savingAliases || !currentOrganizationId"
          @click="saveAliases"
        />
      </div>
    </UPageCard>

    <UPageCard variant="subtle">
      <div class="grid gap-5">
        <UFormField
          label="Email aliases"
          description="Used to match telemetry records when using a different email from your Zeeq organization login."
        >
          <UInputTags
            v-model="emailAliasModel"
            placeholder="personal@example.com"
            class="w-full mt-2"
            size="xl"
            maxlength="320"
            addOnBlur
            addOnPaste
            addOnTab
            delimiter=","
            icon="i-hugeicons-mail-at-sign-01"
            :max="3"
            :disabled="savingAliases"
          />
        </UFormField>

        <USeparator class="my-1" />

        <UFormField
          label="GitHub aliases"
          description="Used to filter your PRs in the pull request inbox."
        >
          <UInputTags
            v-model="gitHubAliasModel"
            placeholder="github-login"
            class="w-full mt-2"
            size="xl"
            maxlength="320"
            addOnBlur
            addOnPaste
            addOnTab
            delimiter=","
            icon="i-hugeicons-github"
            :max="3"
            :disabled="savingAliases"
          />
        </UFormField>
      </div>
    </UPageCard>

    <UAlert
      title="Use aliases for telemetry and PRs"
      description="Aliases are used to match telemetry and pull request authors to the active organization. This allows you to use your personal email (for Anthropic, OpenAI accounts) or GitHub login for telemetry and PRs, while still associating that data with the organization identity used to log in."
      color="info"
      variant="subtle"
    />
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref, watch } from "vue";
import { storeToRefs } from "pinia";
import { useAppStore } from "@/stores/app-store";
import { useOrganizationSettingsStore } from "@/stores/organization-settings-store";

const toast = useToast();
const appStore = useAppStore();
const settingsStore = useOrganizationSettingsStore();
const { user: me, currentOrganization } = storeToRefs(appStore);
const { savingAliases, currentOrganizationId } = storeToRefs(settingsStore);

const emailAliases = ref<string[]>([]);
const gitHubAliases = ref<string[]>([]);
const savedEmailAliases = ref<string[]>([]);
const savedGitHubAliases = ref<string[]>([]);
const maxAliasesPerKind = 3;

const organizationName = computed(
  () => currentOrganization.value?.displayName ?? "the active organization",
);
const emailAliasModel = computed({
  get: () => emailAliases.value,
  set: (value: string[]) => {
    emailAliases.value = value.slice(0, maxAliasesPerKind);
  },
});
const gitHubAliasModel = computed({
  get: () => gitHubAliases.value,
  set: (value: string[]) => {
    gitHubAliases.value = value.slice(0, maxAliasesPerKind);
  },
});

const isDirty = computed(
  () =>
    !sameAliases(emailAliases.value, savedEmailAliases.value) ||
    !sameAliases(gitHubAliases.value, savedGitHubAliases.value),
);

watch(
  () => [me.value?.organizationId, me.value?.aliases] as const,
  () => resetFromMe(),
  { immediate: true },
);

onMounted(async () => {
  try {
    await appStore.fetchUser({ force: true });
  } catch (err: unknown) {
    showError(err instanceof Error ? err.message : "Could not load aliases.");
  }
});

/** Saves the active org alias replacement payload. */
async function saveAliases() {
  try {
    await settingsStore.updateUserAliases({
      emailAliases: emailAliases.value,
      gitHubAliases: gitHubAliases.value,
    });
    resetFromMe();
    toast.add({
      title: "Aliases saved",
      icon: "i-hugeicons-tick-02",
      color: "success",
    });
  } catch (err: unknown) {
    showError(err instanceof Error ? err.message : "Could not save aliases.");
  }
}

/** Resets the draft from the active-org aliases embedded in /me. */
function resetFromMe() {
  const aliases = me.value?.aliases ?? [];
  savedEmailAliases.value = aliases
    .filter((alias) => alias.kind === "email")
    .map((alias) => alias.value);
  savedGitHubAliases.value = aliases
    .filter((alias) => alias.kind === "github")
    .map((alias) => alias.value);
  emailAliases.value = [...savedEmailAliases.value];
  gitHubAliases.value = [...savedGitHubAliases.value];
}

function sameAliases(left: string[], right: string[]): boolean {
  return (
    left.length === right.length &&
    left.every((value, index) => value === right[index])
  );
}

function showError(message: string) {
  toast.add({
    title: "Aliases update failed",
    description: message,
    icon: "i-hugeicons-alert-02",
    color: "error",
  });
}
</script>
