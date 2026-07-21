<template>
  <!--
    Standalone same-domain onboarding prompt. This route intentionally renders
    outside AppLayout so first-login users decide before entering the app shell.
  -->
  <div class="flex min-h-screen items-center justify-center p-4">
    <!-- Invitation decision card: loading, invitation details, and fallback/error states. -->
    <UPageCard
      class="w-full max-w-md"
      :title="cardTitle"
      :description="cardDescription"
      icon="i-hugeicons-user-group"
      :ui="{ footer: 'w-full self-stretch' }"
    >
      <div v-if="loading" class="flex items-center justify-center py-8">
        <UIcon name="i-hugeicons-loading-03" class="size-6 animate-spin" />
      </div>

      <!-- Matched organization and owner context shown before accepting. -->
      <div v-else-if="sameDomainInvitation" class="flex flex-col gap-5">
        <USeparator label="Join organization" />

        <div class="flex items-center gap-3">
          <UAvatar
            :src="sameDomainInvitation.organizationIconUrl ?? undefined"
            :alt="sameDomainInvitation.organizationName"
            icon="i-hugeicons-cube"
            size="lg"
          />
          <div class="min-w-0">
            <p class="truncate text-sm font-medium text-highlighted">
              {{ sameDomainInvitation.organizationName }}
            </p>
            <p class="truncate text-sm text-muted">
              Invited as {{ sameDomainInvitation.role }}
            </p>
          </div>
        </div>

        <USeparator label="Invited by organization owner" />

        <div class="flex items-center gap-3">
          <UAvatar
            :src="sameDomainInvitation.ownerPictureUrl ?? undefined"
            :alt="sameDomainInvitation.ownerDisplayName"
            icon="i-hugeicons-user"
            size="md"
          />
          <div class="min-w-0">
            <p class="truncate text-sm font-medium text-highlighted">
              {{ sameDomainInvitation.ownerDisplayName }}
            </p>
            <p class="truncate text-sm text-muted">
              {{ sameDomainInvitation.ownerEmail || "Organization owner" }}
            </p>
          </div>
        </div>
      </div>

      <UAlert
        v-else-if="errorMessage"
        icon="i-hugeicons-alert-02"
        color="error"
        variant="subtle"
        :description="errorMessage"
      />

      <template #footer>
        <!-- Decision actions: accept joins/defaults the org; not-now only suppresses this session. -->
        <div v-if="sameDomainInvitation" class="flex w-full flex-col gap-2">
          <UButton
            block
            label="Join team"
            icon="i-hugeicons-tick-02"
            color="neutral"
            :loading="joining"
            :disabled="notNowSaving"
            @click="acceptInvitation"
          />
          <ZeeqPopConfirm
            title="Not now"
            body="You can accept this invitation later from your memberships."
            confirm-label="Not now"
            confirm-color="neutral"
            confirm-icon="i-hugeicons-clock-01"
            block
            label="Not now"
            icon="i-hugeicons-clock-01"
            color="neutral"
            variant="ghost"
            :disabled="joining || notNowSaving"
            @confirm="dismissInvitation"
          />
        </div>

        <UButton
          v-else
          block
          label="Continue"
          icon="i-hugeicons-arrow-right-01"
          color="neutral"
          variant="subtle"
          :disabled="loading"
          @click="continueWithoutInvitation"
        />
      </template>
    </UPageCard>
  </div>
</template>

<script setup lang="ts">
import { storeToRefs } from "pinia";
import { useRoute, useRouter } from "vue-router";
import ZeeqPopConfirm from "@/components/ZeeqPopConfirm.vue";
import { useAppStore } from "@/stores/app-store";

const route = useRoute();
const router = useRouter();
const toast = useToast();
const appStore = useAppStore();
const {
  sameDomainInvitation,
  sameDomainInvitationLoading,
  sameDomainInvitationError,
} = storeToRefs(appStore);

const joining = ref(false);
const notNowSaving = ref(false);

// Keep visual loading tied to invitation discovery only when no details are
// already available, avoiding a blank card during background refresh.
const loading = computed(
  () => sameDomainInvitationLoading.value && !sameDomainInvitation.value,
);

// Return URLs are route-local only; the join page must never become an open
// redirect target after the user accepts or skips the prompt.
const safeReturnUrl = computed(() =>
  sanitizeReturnUrl(readSingleQueryValue(route.query.returnUrl)),
);

// Header copy is derived from loaded invitation details so the card remains
// meaningful while degraded/fallback states use generic wording.
const cardTitle = computed(() =>
  sameDomainInvitation.value
    ? `Join ${sameDomainInvitation.value.organizationName}`
    : "Join your team",
);
const cardDescription = computed(() =>
  sameDomainInvitation.value
    ? "Your email domain matches an organization in Zeeq."
    : "No matching invitation is available.",
);
const errorMessage = computed(() => sameDomainInvitationError.value);

// Load invitation state before /me because new same-domain users can have an
// inactive personal org until they accept the matched team invitation.
onMounted(async () => {
  try {
    await appStore.fetchSameDomainInvitation({
      force: true,
      allowWithoutAuthenticated: true,
    });

    if (sameDomainInvitation.value) {
      return;
    }

    await appStore.fetchUser({ force: true });
    if (!appStore.isAuthenticated) {
      await router.replace({
        path: "/login",
        query: { returnUrl: route.fullPath },
      });
    }
  } catch (err: unknown) {
    toast.add({
      title: "Could not load invitation",
      description: err instanceof Error ? err.message : undefined,
      icon: "i-hugeicons-alert-02",
      color: "error",
    });
  }
});

// Accepting same-domain onboarding makes the invited org default server-side,
// switches the active org cookie, then resumes the sanitized original route.
async function acceptInvitation() {
  if (!sameDomainInvitation.value) {
    return;
  }

  joining.value = true;

  try {
    await appStore.acceptSameDomainInvitationAsDefault(
      sameDomainInvitation.value.invitationId,
    );
    await router.replace(safeReturnUrl.value);
  } catch (err: unknown) {
    toast.add({
      title: "Could not join team",
      description: err instanceof Error ? err.message : undefined,
      icon: "i-hugeicons-alert-02",
      color: "error",
    });
  } finally {
    joining.value = false;
  }
}

// "Not now" is session-only suppression. It deliberately does not decline the
// backend invitation, so the standard membership invitation list still shows it.
async function dismissInvitation() {
  if (!sameDomainInvitation.value) {
    await continueWithoutInvitation();
    return;
  }

  notNowSaving.value = true;
  try {
    appStore.suppressSameDomainInvitation(
      sameDomainInvitation.value.invitationId,
    );
    await router.replace("/settings/organization");
  } finally {
    notNowSaving.value = false;
  }
}

// Used for fallback states where no same-domain invitation is currently shown.
async function continueWithoutInvitation() {
  await router.replace(safeReturnUrl.value);
}

// Allows only same-origin relative paths and prevents returning to this prompt,
// which would otherwise create a redirect loop.
function sanitizeReturnUrl(value: string | undefined): string {
  if (!value || !value.startsWith("/") || value.startsWith("//")) {
    return "/";
  }

  if (value.includes("://") || value.startsWith("/join-your-team")) {
    return "/";
  }

  return value;
}

// Vue Router query values can be arrays; this flow only accepts one value.
function readSingleQueryValue(value: unknown): string | undefined {
  return typeof value === "string" ? value : undefined;
}
</script>
