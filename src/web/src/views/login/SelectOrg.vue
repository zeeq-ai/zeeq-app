<template>
  <!--
    DCR org picker. Shown when an authenticated user with more than one active
    organization starts a Dynamic Client Registration flow. Bound to the same
    origin as /connect/authorize (the interactive-auth origin) so the identity
    cookie travels with the redirect from the authorize endpoint.
  -->
  <div class="flex flex-col items-center justify-center gap-4 p-4 min-h-screen">
    <UPageCard class="w-full max-w-md">
      <div class="flex flex-col gap-4">
        <div class="flex flex-col gap-1">
          <h2 class="text-lg font-semibold text-highlighted">
            Choose an organization
          </h2>
          <p class="text-sm text-dimmed">
            Select which organization this client should be scoped to. You can
            switch later by re-running the sign-in flow.
          </p>
        </div>

        <div v-if="loading" class="flex items-center justify-center py-8">
          <UIcon name="i-hugeicons-loading-03" class="size-6 animate-spin" />
        </div>

        <div
          v-else-if="errorMessage"
          class="flex flex-col items-center gap-3 py-4"
        >
          <UIcon name="i-hugeicons-alert-02" class="size-8 text-error" />
          <p class="text-sm text-error text-center">{{ errorMessage }}</p>
          <UButton
            v-if="safeReturnUrl"
            label="Continue"
            color="neutral"
            variant="subtle"
            icon="i-hugeicons-arrow-right-01"
            @click="() => goToReturnUrl()"
          />
        </div>

        <UForm
          v-else-if="selectableOrgs.length > 0"
          :state="formState"
          class="flex flex-col gap-4"
          @submit.prevent="confirmSelection"
        >
          <div class="flex flex-col gap-2">
            <button
              v-for="org in selectableOrgs"
              :key="org.id"
              type="button"
              class="flex items-center gap-3 rounded-md border p-3 text-left transition hover:bg-elevated/50"
              :class="
                formState.selectedOrgId === org.id
                  ? 'border-primary bg-primary/5'
                  : 'border-default'
              "
              @click="formState.selectedOrgId = org.id"
            >
              <UAvatar
                :src="org.iconUrl ?? undefined"
                :alt="org.displayName"
                icon="i-hugeicons-cube"
                size="md"
              />
              <div class="flex flex-1 flex-col">
                <span class="font-medium text-highlighted">
                  {{ org.displayName }}
                </span>
                <span class="text-xs text-dimmed capitalize">
                  {{ org.role }}
                  <span v-if="org.isDefault"> · default</span>
                </span>
              </div>
              <UIcon
                v-if="formState.selectedOrgId === org.id"
                name="i-hugeicons-tick-02"
                class="size-5 text-primary"
              />
            </button>
          </div>

          <UButton
            type="submit"
            block
            label="Continue"
            color="neutral"
            variant="subtle"
            icon="i-hugeicons-arrow-right-01"
            :loading="submitting"
            :disabled="!formState.selectedOrgId"
          />
        </UForm>

        <div v-else class="flex flex-col items-center gap-3 py-4">
          <UIcon name="i-hugeicons-alert-02" class="size-8 text-warning" />
          <p class="text-sm text-dimmed text-center">
            No active organizations are available for your account.
          </p>
          <UButton
            v-if="safeReturnUrl"
            label="Back to sign-in"
            icon="i-hugeicons-arrow-left-01"
            color="neutral"
            variant="subtle"
            to="/login"
          />
        </div>
      </div>
    </UPageCard>
  </div>
</template>

<script setup lang="ts">
import { useRoute } from "vue-router";
import { Memberships, type OrgSummary } from "@/api/generated";
import { useAppStore } from "@/stores/app-store";
import { ZeeqApiError } from "@/api/zeeq-api-client";

const route = useRoute();
const appStore = useAppStore();
const toast = useToast();

const loading = ref(true);
const submitting = ref(false);
const errorMessage = ref<string | null>(null);

const formState = reactive<{ selectedOrgId: string | null }>({
  selectedOrgId: null,
});

/**
 * The /connect/authorize URL to resume after the org is selected. The authorize
 * endpoint passes it as ?returnUrl=/connect/authorize?... Only same-origin
 * relative /connect/authorize URLs are accepted to prevent open redirect.
 */
const rawReturnUrl = computed(() => {
  const value = route.query.returnUrl;
  return typeof value === "string" ? value : null;
});

const safeReturnUrl = computed(() =>
  sanitizeAuthorizeReturnUrl(rawReturnUrl.value),
);

/**
 * Active organizations from /me. The authorize endpoint only redirects here when
 * there is more than one, but the list is derived defensively from /me.
 */
const selectableOrgs = computed<OrgSummary[]>(() =>
  (appStore.user?.organizations ?? []).filter((org) => org.status === "Active"),
);

onMounted(async () => {
  try {
    await appStore.fetchUser({ force: true });

    // Pre-select the default org (or the current active org) for convenience.
    const defaultOrg =
      selectableOrgs.value.find((org) => org.isDefault) ??
      selectableOrgs.value.find(
        (org) => org.id === appStore.user?.organizationId,
      );
    formState.selectedOrgId = defaultOrg?.id ?? null;

    // If there is no returnUrl to resume, there is nothing useful to do here.
    if (!safeReturnUrl.value) {
      errorMessage.value =
        "Missing return URL. Please restart the sign-in flow.";
    }
  } catch {
    errorMessage.value = "Could not load your organizations. Please try again.";
  } finally {
    loading.value = false;
  }
});

/**
 * Switches the server-side active organization (re-issues the identity cookie on
 * this origin), then resumes the original /connect/authorize request. The store
 * variant reloads the current page, so call the generated client directly and
 * navigate to the returnUrl instead.
 */
async function confirmSelection() {
  if (!formState.selectedOrgId || !safeReturnUrl.value) {
    return;
  }

  submitting.value = true;
  errorMessage.value = null;

  try {
    await Memberships.switchOrganization(formState.selectedOrgId);
    // Append orgId to the return URL so /connect/authorize can detect that org selection
    // is complete and skip the picker redirect, breaking the otherwise-infinite loop.
    goToReturnUrl(formState.selectedOrgId);
  } catch (err: unknown) {
    submitting.value = false;
    if (err instanceof ZeeqApiError && err.status === 404) {
      errorMessage.value = "You are not a member of that organization.";
    } else {
      errorMessage.value =
        err instanceof Error ? err.message : "Could not switch organization.";
      toast.add({
        title: "Could not switch organization",
        description: errorMessage.value,
        color: "error",
      });
    }
  }
}

function goToReturnUrl(orgId?: string | null) {
  if (safeReturnUrl.value) {
    const url = orgId
      ? safeReturnUrl.value +
        (safeReturnUrl.value.includes("?") ? "&" : "?") +
        "orgId=" +
        encodeURIComponent(orgId)
      : safeReturnUrl.value;
    window.location.href = url;
  }
}

/**
 * Accepts only same-origin relative /connect/authorize URLs. Rejects absolute
 * URLs, scheme/host references, and anything that would redirect off-origin.
 */
function sanitizeAuthorizeReturnUrl(value: string | null): string | null {
  if (!value) {
    return null;
  }

  // Must start with /connect/authorize (the only legitimate resume target).
  if (!value.startsWith("/connect/authorize")) {
    return null;
  }

  // Reject anything that looks like a scheme://authority reference.
  if (/^\/\//.test(value) || value.includes("://")) {
    return null;
  }

  return value;
}
</script>
