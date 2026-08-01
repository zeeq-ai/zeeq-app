import { defineStore, acceptHMRUpdate } from "pinia";
import {
  SystemActivationKeys,
  type CreateSystemActivationKeyRequest,
  type CreateSystemActivationKeyResponse,
  type OrganizationActivationKeyStatus,
  type SystemActivationKeyResponse,
} from "@/api/generated";

export type SystemActivationKeyListQuery = {
  page: number;
  pageSize: number;
  q: string;
  status: OrganizationActivationKeyStatus | null;
};

const defaultListQuery: SystemActivationKeyListQuery = {
  page: 1,
  pageSize: 25,
  q: "",
  status: null,
};

export const useSystemActivationKeyStore = defineStore(
  "system-activation-key-store",
  () => {
    const keys = ref<SystemActivationKeyResponse[]>([]);
    const totalCount = ref(0);
    const page = ref(defaultListQuery.page);
    const pageSize = ref(defaultListQuery.pageSize);
    const query = ref(defaultListQuery.q);
    const status = ref<OrganizationActivationKeyStatus | null>(
      defaultListQuery.status,
    );
    const loading = ref(false);
    const creating = ref(false);
    const revoking = ref<string | null>(null);
    const createdKey = ref<CreateSystemActivationKeyResponse | null>(null);
    const error = ref<string | null>(null);

    let requestId = 0;

    function setListQuery(next: Partial<SystemActivationKeyListQuery>) {
      page.value = next.page ?? page.value;
      pageSize.value = next.pageSize ?? pageSize.value;
      query.value = next.q ?? query.value;
      if ("status" in next) {
        status.value = next.status ?? null;
      }
    }

    async function loadKeys(next: Partial<SystemActivationKeyListQuery> = {}) {
      setListQuery(next);

      const currentRequestId = ++requestId;
      loading.value = true;
      error.value = null;

      try {
        const response = await SystemActivationKeys.listSystemActivationKeys({
          page: page.value,
          pageSize: pageSize.value,
          q: query.value || undefined,
          status: status.value ?? undefined,
        });

        if (currentRequestId !== requestId) {
          return response;
        }

        keys.value = response.items;
        page.value = toNumber(response.page);
        pageSize.value = toNumber(response.pageSize);
        totalCount.value = toNumber(response.totalCount);

        return response;
      } catch (err: unknown) {
        if (currentRequestId === requestId) {
          error.value = toErrorMessage(err, "Could not load activation keys.");
        }
        throw err;
      } finally {
        if (currentRequestId === requestId) {
          loading.value = false;
        }
      }
    }

    async function createKey(request: CreateSystemActivationKeyRequest) {
      creating.value = true;
      error.value = null;

      try {
        createdKey.value =
          await SystemActivationKeys.createSystemActivationKey(request);
        await loadKeys({ page: 1 });
        return createdKey.value;
      } catch (err: unknown) {
        error.value = toErrorMessage(err, "Could not create activation key.");
        throw err;
      } finally {
        creating.value = false;
      }
    }

    async function revokeKey(keyId: string) {
      revoking.value = keyId;
      error.value = null;

      try {
        const revoked =
          await SystemActivationKeys.revokeSystemActivationKey(keyId);
        patchKey(revoked);
        return revoked;
      } catch (err: unknown) {
        error.value = toErrorMessage(err, "Could not revoke activation key.");
        throw err;
      } finally {
        revoking.value = null;
      }
    }

    function clearCreatedKey() {
      createdKey.value = null;
    }

    function patchKey(key: SystemActivationKeyResponse) {
      const index = keys.value.findIndex((item) => item.id === key.id);
      if (index >= 0) {
        keys.value[index] = key;
      }
    }

    return {
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
      setListQuery,
      loadKeys,
      createKey,
      revokeKey,
      clearCreatedKey,
    };
  },
);

function toNumber(value: number | string) {
  return typeof value === "number" ? value : Number(value);
}

function toErrorMessage(err: unknown, fallback: string): string {
  return err instanceof Error ? err.message : fallback;
}

if (import.meta.hot) {
  import.meta.hot.accept(
    acceptHMRUpdate(useSystemActivationKeyStore, import.meta.hot),
  );
}
