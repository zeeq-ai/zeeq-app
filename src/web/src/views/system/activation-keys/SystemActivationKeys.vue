<template>
  <div class="flex min-h-0 flex-1 flex-col gap-4">
    <UAlert
      v-if="error"
      title="Activation keys unavailable"
      :description="error"
      icon="i-hugeicons-alert-02"
      color="error"
      variant="subtle"
    />

    <UPageCard
      variant="subtle"
      class="min-w-0 overflow-hidden"
      :ui="{ container: 'p-0 sm:p-0 gap-y-0' }"
    >
      <div
        class="flex flex-col gap-3 border-b border-default p-4 lg:flex-row lg:items-center lg:justify-between"
      >
        <div class="min-w-0">
          <h2 class="text-base font-semibold text-highlighted">
            Activation keys
          </h2>
          <p class="mt-1 text-sm text-muted">{{ countLabel }}</p>
        </div>

        <div class="flex flex-col gap-2 sm:flex-row sm:items-center">
          <UInput
            :model-value="query"
            icon="i-hugeicons-search-01"
            placeholder="Search keys"
            aria-label="Search activation keys"
            class="min-w-0 sm:w-72"
            @update:model-value="emitSearch"
          />
          <USelect
            :model-value="status"
            :items="statusItems"
            placeholder="Status"
            class="w-full sm:w-40"
            @update:model-value="emitStatus"
          />
          <UTooltip text="Refresh keys">
            <UButton
              icon="i-hugeicons-refresh"
              color="neutral"
              variant="subtle"
              aria-label="Refresh keys"
              :loading="loading"
              @click="refresh"
            />
          </UTooltip>
          <UButton
            icon="i-hugeicons-plus-sign"
            variant="subtle"
            @click="openCreateSlideover"
          >
            Create
          </UButton>
        </div>
      </div>

      <div class="min-w-0 overflow-x-auto">
        <UTable
          :data="keys"
          :columns="columns"
          :loading="loading"
          class="min-w-250"
        >
          <template #id-cell="{ row }">
            <div class="font-mono text-xs text-highlighted">
              {{ row.original.id }}
            </div>
            <div class="truncate text-xs text-muted">
              {{ row.original.note ?? "No note" }}
            </div>
          </template>

          <template #status-cell="{ row }">
            <UBadge
              :label="row.original.status"
              :color="statusColor(row.original.status)"
              variant="subtle"
            />
          </template>

          <template #created-cell="{ row }">
            <div class="text-sm text-default">
              {{ formatDate(row.original.createdAtUtc) }}
            </div>
            <div class="text-xs text-default">
              {{ row.original.createdByDisplayName }}
            </div>
            <div class="text-xs text-muted">
              {{ row.original.createdByUserId }}
            </div>
          </template>

          <template #expires-cell="{ row }">
            {{ formatDate(row.original.expiresAtUtc) }}
          </template>

          <template #activated-cell="{ row }">
            <div v-if="row.original.activatedAtUtc" class="text-sm">
              {{ formatDate(row.original.activatedAtUtc) }}
            </div>
            <div class="truncate text-xs text-muted">
              {{ row.original.activatedOrganizationId ?? "" }}
            </div>
          </template>

          <template #actions-cell="{ row }">
            <UButton
              v-if="row.original.status === 'Available'"
              label="Revoke"
              icon="i-hugeicons-cancel-01"
              color="neutral"
              variant="subtle"
              size="xs"
              :loading="revoking === row.original.id"
              @click="revoke(row.original.id)"
            />
          </template>

          <template #empty>
            <UEmpty
              icon="i-hugeicons-shield-key"
              title="No activation keys found"
            />
          </template>
        </UTable>
      </div>

      <div
        v-if="totalCount > 0"
        class="flex flex-col gap-3 border-t border-default p-4 sm:flex-row sm:items-center sm:justify-between"
      >
        <p class="text-sm text-muted">{{ pageRangeLabel }}</p>
        <UPagination
          :page
          :items-per-page="pageSize"
          :total="totalCount"
          variant="soft"
          active-variant="soft"
          @update:page="updatePage"
        />
      </div>
    </UPageCard>

    <USlideover v-model:open="createOpen" title="Activation key">
      <template #body>
        <div class="flex flex-col gap-4">
          <UForm :state="form" class="flex flex-col gap-4" @submit="createKey">
            <UFormField label="Note" name="note">
              <UTextarea
                v-model="form.note"
                class="w-full"
                :rows="4"
                maxlength="500"
                placeholder="Operator note"
              />
            </UFormField>

            <UFormField
              label="Expires in days"
              name="expiresInDays"
              :hint="expiresInDaysLabel"
            >
              <USlider
                v-model="form.expiresInDays"
                :min="activationKeyLifetimeBounds.min"
                :max="activationKeyLifetimeBounds.max"
                :step="activationKeyLifetimeBounds.step"
                tooltip
              />
              <div class="mt-2 flex justify-between text-xs text-muted">
                <span>{{ activationKeyLifetimeBounds.min }} day</span>
                <span>{{ activationKeyLifetimeBounds.max }} days</span>
              </div>
            </UFormField>

            <div class="flex justify-end gap-2">
              <UButton
                color="neutral"
                variant="subtle"
                @click="createOpen = false"
              >
                Close
              </UButton>
              <UButton
                type="submit"
                icon="i-hugeicons-plus-sign"
                variant="subtle"
                :loading="creating"
              >
                Create key
              </UButton>
            </div>
          </UForm>

          <template v-if="createdKey">
            <USeparator class="my-2" label="Org activation key" />

            <div class="flex flex-col gap-4">
              <UAlert
                title="Copy this key now"
                description="This activation key is only shown once. Copy it before closing this panel."
                icon="i-hugeicons-key-01"
                color="warning"
                variant="subtle"
              />

              <UTextarea
                :model-value="createdKey.key"
                class="w-full"
                readonly
                :rows="3"
                :ui="{ base: 'font-mono text-xs break-all' }"
              />

              <div class="flex justify-end">
                <UButton
                  :label="copiedCreatedKey ? 'Copied' : 'Copy'"
                  icon="i-hugeicons-copy-01"
                  color="neutral"
                  variant="subtle"
                  @click="copyCreatedKey"
                />
              </div>
            </div>
          </template>
        </div>
      </template>
    </USlideover>
  </div>
</template>

<script setup lang="ts">
import type { TableColumn } from "@nuxt/ui";
import { useClipboard, useDebounceFn } from "@vueuse/core";
import { storeToRefs } from "pinia";
import { useRoute, useRouter } from "vue-router";
import {
  organizationActivationKeyStatusEnum,
  type OrganizationActivationKeyStatus,
  type SystemActivationKeyResponse,
} from "@/api/generated";
import { useSystemActivationKeyStore } from "@/stores/system-activation-key-store";
import type { LocationQueryRaw, LocationQueryValue } from "vue-router";

type StatusOption = {
  label: OrganizationActivationKeyStatus;
  value: OrganizationActivationKeyStatus;
};

const statusOptions = Object.values(
  organizationActivationKeyStatusEnum,
) as OrganizationActivationKeyStatus[];
const statusOptionItems: StatusOption[] = statusOptions.map((value) => ({
  label: value,
  value,
}));

const defaultPage = 1;
const defaultPageSize = 25;

const route = useRoute();
const router = useRouter();
const toast = useToast();
const { copied: copiedCreatedKey, copy: copyToClipboard } = useClipboard({
  copiedDuring: 1500,
  legacy: true,
});
const store = useSystemActivationKeyStore();
const {
  keys,
  totalCount,
  page,
  pageSize,
  query,
  status,
  loading,
  creating,
  revoking,
  createdKey,
  error,
} = storeToRefs(store);

const createOpen = ref(false);
// NOTE: These bounds intentionally mirror current server defaults until
// system-admin capability/config discovery exposes deployment-specific values.
const activationKeyLifetimeBounds = {
  min: 1,
  default: 90,
  max: 365,
  step: 1,
} as const;
const form = reactive<{ note: string; expiresInDays: number }>({
  note: "",
  expiresInDays: activationKeyLifetimeBounds.default,
});

const columns: TableColumn<SystemActivationKeyResponse>[] = [
  { id: "id", header: "Key" },
  { id: "status", header: "Status" },
  { id: "created", header: "Created" },
  { id: "expires", header: "Expires" },
  { id: "activated", header: "Activated" },
  { id: "actions", header: "" },
];

const statusItems = [{ label: "All", value: null }, ...statusOptionItems];

const countLabel = computed(() =>
  totalCount.value === 1
    ? "1 activation key"
    : `${totalCount.value} activation keys`,
);

const pageRangeLabel = computed(() => {
  if (totalCount.value === 0) {
    return "0 of 0";
  }

  const start = (page.value - 1) * pageSize.value + 1;
  const end = Math.min(totalCount.value, page.value * pageSize.value);

  return `${start}-${end} of ${totalCount.value}`;
});

const expiresInDaysLabel = computed(() => `${form.expiresInDays} days`);

watch(
  () => route.query,
  () => {
    store.setListQuery({
      page: readPositiveInt(route.query.page, defaultPage),
      pageSize: readPositiveInt(route.query.pageSize, defaultPageSize),
      q: readString(route.query.q),
      status: readStatus(route.query.status),
    });
    void store.loadKeys().catch(() => {});
  },
  { immediate: true },
);

const emitSearch = useDebounceFn(async (value: string | number) => {
  await replaceQuery({ page: undefined, q: String(value).trim() || undefined });
}, 250);

async function emitStatus(value: string | number | null) {
  await replaceQuery({
    page: undefined,
    status: readStatus(value),
  });
}

async function updatePage(nextPage: number) {
  await replaceQuery({
    page: nextPage === defaultPage ? undefined : String(nextPage),
  });
}

async function refresh() {
  try {
    await store.loadKeys();
  } catch (err: unknown) {
    showError("Could not refresh activation keys.", err);
  }
}

function openCreateSlideover() {
  store.clearCreatedKey();
  createOpen.value = true;
}

async function createKey() {
  try {
    await store.createKey({
      note: form.note.trim() || null,
      expiresInDays: form.expiresInDays,
    });
    form.note = "";
    form.expiresInDays = activationKeyLifetimeBounds.default;
    toast.add({
      title: "Activation key created",
      icon: "i-hugeicons-tick-02",
      color: "success",
    });
  } catch (err: unknown) {
    showError("Could not create activation key.", err);
  }
}

async function revoke(keyId: string) {
  try {
    await store.revokeKey(keyId);
    toast.add({
      title: "Activation key revoked",
      icon: "i-hugeicons-tick-02",
      color: "success",
    });
  } catch (err: unknown) {
    showError("Could not revoke activation key.", err);
  }
}

async function copyCreatedKey() {
  if (!createdKey.value) {
    return;
  }

  await copyToClipboard(createdKey.value.key);
  toast.add({ title: "Copied", icon: "i-hugeicons-copy-01", color: "success" });
}

async function replaceQuery(changes: Record<string, LocationQueryRaw[string]>) {
  await router.replace({ query: cleanQuery({ ...route.query, ...changes }) });
}

function cleanQuery(query: Record<string, unknown>): LocationQueryRaw {
  const clean: LocationQueryRaw = {};

  for (const [key, value] of Object.entries(query)) {
    if (
      (typeof value === "string" && value !== "") ||
      typeof value === "number"
    ) {
      clean[key] = value;
    }
  }

  return clean;
}

function readPositiveInt(
  value: LocationQueryValue | LocationQueryValue[],
  fallback: number,
) {
  const rawValue = readNullableString(value);
  const numericValue = rawValue === null ? Number.NaN : Number(rawValue);

  return Number.isInteger(numericValue) && numericValue > 0
    ? numericValue
    : fallback;
}

function readString(value: LocationQueryValue | LocationQueryValue[]) {
  return readNullableString(value) ?? "";
}

function readNullableString(value: LocationQueryValue | LocationQueryValue[]) {
  if (Array.isArray(value)) {
    return value[0] ?? null;
  }

  return value ?? null;
}

function readStatus(value: unknown): OrganizationActivationKeyStatus | null {
  if (typeof value !== "string") {
    return null;
  }

  return statusOptions.some((option) => option === value)
    ? (value as OrganizationActivationKeyStatus)
    : null;
}

function statusColor(statusValue: OrganizationActivationKeyStatus) {
  if (statusValue === "Available") {
    return "success";
  }

  if (statusValue === "Activated") {
    return "primary";
  }

  if (statusValue === "Expired") {
    return "warning";
  }

  return "neutral";
}

function formatDate(value: Date | string | null) {
  if (!value) {
    return "";
  }

  return new Date(value).toLocaleDateString(undefined, {
    year: "numeric",
    month: "short",
    day: "numeric",
  });
}

function showError(title: string, err: unknown) {
  toast.add({
    title,
    description: err instanceof Error ? err.message : undefined,
    icon: "i-hugeicons-alert-02",
    color: "error",
  });
}
</script>
