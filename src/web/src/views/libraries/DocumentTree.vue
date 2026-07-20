<template>
  <!--
  Document tree: renders a UTree from a view-model computed over flat document
  paths. Every node carries pre-computed context actions so the template never
  calls functions to derive data (KB: view models via computed).
  -->
  <div class="flex flex-col h-full">
    <!-- Header -->
    <div
      class="flex h-[45px] items-center justify-between px-3 border-b border-default"
    >
      <span class="text-sm font-medium">Documents</span>
      <div class="flex items-center gap-1">
        <UButton
          v-if="hasLibrary"
          icon="i-hugeicons-refresh"
          size="xs"
          color="neutral"
          variant="ghost"
          :loading="loading"
          aria-label="Refresh documents"
          @click="emits('refresh')"
        />
        <UButton
          v-if="hasLibrary && selectedFolderPath"
          icon="i-hugeicons-plus-sign"
          size="xs"
          color="neutral"
          variant="ghost"
          aria-label="Add document to selected folder"
          @click="emits('add', selectedFolderPath)"
        />
      </div>
    </div>

    <!-- Tree -->
    <div class="flex-1 overflow-y-auto">
      <UTree
        v-if="treeItems.length > 0"
        v-model:expanded="expandedNodeIds"
        size="sm"
        :model-value="activeTreeNode ?? undefined"
        :get-key="getNodeKey"
        :items="treeItems"
        :loading="loading"
        @select="(_, item: DocNode) => onSelectNode(item)"
      >
        <!--
        Custom node: icon + label + origin badge + context menu.
        node.actions is pre-computed in the view model — no template function calls.
        -->
        <template #item="{ item: node }">
          <div class="flex items-center justify-between w-full gap-1">
            <div class="flex items-center gap-1.5 min-w-0">
              <UIcon
                :name="
                  node.isRoot
                    ? 'i-hugeicons-folder-open'
                    : node.isFolder
                      ? 'i-hugeicons-folder-01'
                      : (node as DocNode).origin === 'remote'
                        ? 'i-hugeicons-file-cloud'
                        : 'i-hugeicons-file-01'
                "
                class="size-4 shrink-0 text-neutral-400"
              />
              <span class="truncate text-sm">{{ node.label }}</span>

              <!-- Muted marker: document is excluded from code-review agents -->
              <UTooltip
                v-if="(node as DocNode).excludedFromCodeReviews"
                text="Excluded from code reviews"
                :delay-duration="0"
              >
                <UIcon
                  name="i-hugeicons-view-off-slash"
                  class="size-3.5 shrink-0 text-neutral-400"
                  aria-label="Excluded from code reviews"
                />
              </UTooltip>
            </div>
            <UDropdownMenu :items="(node as DocNode).actions">
              <UButton
                icon="i-hugeicons-more-vertical"
                size="xs"
                color="neutral"
                variant="ghost"
                aria-label="Actions"
                @click.stop
              />
            </UDropdownMenu>
          </div>
        </template>
      </UTree>

      <UEmpty
        v-else-if="!loading"
        title="No documents"
        variant="naked"
        description="Create your first document to get started."
        icon="i-hugeicons-file-01"
      />
    </div>

    <div class="shrink-0 px-2 pb-2 pt-0">
      <UInput
        v-model="searchQuery"
        placeholder="Filter documents"
        aria-label="Filter documents"
        size="sm"
        class="w-full"
      >
        <template #trailing>
          <UButton
            v-if="searchQuery"
            icon="i-hugeicons-cancel-01"
            size="xs"
            color="neutral"
            variant="ghost"
            aria-label="Clear filter"
            @click="clearSearch"
          />
        </template>
      </UInput>
    </div>
  </div>
</template>

<script setup lang="ts">
import { useFuse } from "@vueuse/integrations/useFuse";
import type { DocumentResponse } from "@/api/generated/types/DocumentResponse";

const props = defineProps<{
  documents: DocumentResponse[];
  loading: boolean;
  hasLibrary: boolean;
  activePath: string | null;
}>();

const emits = defineEmits<{
  select: [path: string];
  folderSelect: [path: string];
  add: [folderPath: string];
  rename: [oldPath: string];
  delete: [path: string];
  refresh: [];
}>();

const userExpandedNodeIds = ref<string[]>([]);
const searchQuery = ref("");

const fuseDocuments = computed(() => props.documents);
const { results: fuseResults } = useFuse(searchQuery, fuseDocuments, {
  fuseOptions: {
    keys: ["path", "title", "keywords"],
    threshold: 0.35,
    ignoreLocation: true,
  },
});

const displayedDocuments = computed(() => {
  if (!searchQuery.value.trim()) return props.documents;
  return fuseResults.value.map((result) => result.item);
});

function clearSearch() {
  searchQuery.value = "";
}

// ── View model ──────────────────────────────────────────────────────────

/** Action descriptor pre-computed for every tree node. */
type NodeAction = {
  label: string;
  icon: string;
  onSelect: () => void;
};

/**
 * Tree node view model with pre-computed actions so the template never
 * calls functions to derive data (KB: project shape in computed).
 */
type DocNode = {
  id: string;
  label: string;
  isFolder: boolean;
  /** Leaf: the document's full path. Folder: the folder's logical path. */
  path: string;
  /** Virtual top-level node. */
  isRoot?: boolean;
  /** Nuxt UI Tree initial expansion hint for folder nodes. */
  defaultExpanded?: boolean;
  /** Leaf only. */
  origin?: "local" | "remote";
  /** Leaf only: hidden from code-review agents' list/search tools. */
  excludedFromCodeReviews?: boolean;
  children?: DocNode[];
  /** Pre-computed context-menu actions for this node. */
  actions: NodeAction[];
};

/**
 * Builds a nested folder tree with pre-computed actions for every node.
 * /guides/nested/doc.md → guides/ > nested/ > doc.md
 */
const treeItems = computed<DocNode[]>(() => {
  return buildTreeItems(displayedDocuments.value);
});

const defaultExpandedNodeIds = ref(new Set<string>());

watch(
  treeItems,
  (items) => {
    const defaultIds = collectDefaultExpandedNodeIds(items);
    const newIds = defaultIds.filter(
      (id) => !defaultExpandedNodeIds.value.has(id),
    );

    if (newIds.length === 0) return;

    defaultExpandedNodeIds.value = new Set([
      ...defaultExpandedNodeIds.value,
      ...newIds,
    ]);
    userExpandedNodeIds.value = Array.from(
      new Set([...userExpandedNodeIds.value, ...newIds]),
    );
  },
  { immediate: true },
);

function buildTreeItems(documents: DocumentResponse[]) {
  const root: Record<string, DocNode> = {};

  for (const doc of documents) {
    const parts = doc.path.split("/").filter(Boolean);
    let current = root;

    for (let i = 0; i < parts.length; i++) {
      const part = parts[i];
      const isLeaf = i === parts.length - 1;
      const fullPath = "/" + parts.slice(0, i + 1).join("/");

      if (!current[part]) {
        const parentPath = "/" + parts.slice(0, i).join("/") || "/";

        current[part] = isLeaf
          ? makeLeafNode(fullPath, doc)
          : makeFolderNode(fullPath, parentPath);

        if (!isLeaf) {
          current = current[part].children! as unknown as Record<
            string,
            DocNode
          >;
        }
      } else if (!isLeaf) {
        current = current[part].children! as unknown as Record<string, DocNode>;
      }
    }
  }

  const children = flattenRecordTree(root);

  return children.length > 0 ? [makeRootNode(children)] : [];
}

const activeTreeNode = computed(() => {
  if (!props.activePath) return null;
  return findNodeByPath(treeItems.value, props.activePath);
});

const selectedFolderPath = computed(() => {
  return activeTreeNode.value?.isFolder ? activeTreeNode.value.path : null;
});

const activeAncestorNodeIds = computed(() => {
  if (!props.activePath || !activeTreeNode.value) return [];
  return ["/", ...getAncestorFolderPaths(props.activePath)];
});

const searchAncestorNodeIds = computed(() => {
  if (!searchQuery.value.trim()) return [];

  return displayedDocuments.value.flatMap((document) => [
    "/",
    ...getAncestorFolderPaths(document.path),
  ]);
});

const expandedNodeIds = computed({
  get() {
    return Array.from(
      new Set([
        "/",
        ...userExpandedNodeIds.value,
        ...activeAncestorNodeIds.value,
        ...searchAncestorNodeIds.value,
      ]),
    );
  },

  set(ids: string[]) {
    userExpandedNodeIds.value = ids;
  },
});

function makeRootNode(children: DocNode[]): DocNode {
  return {
    id: "/",
    label: "(root)",
    isFolder: true,
    isRoot: true,
    defaultExpanded: true,
    path: "/",
    children,
    actions: [
      {
        label: "Add document",
        icon: "i-hugeicons-plus-sign",
        onSelect: () => emits("add", "/"),
      },
    ],
  };
}

/** Creates a leaf node with pre-computed actions (add-sibling, delete). */
function makeLeafNode(path: string, doc: DocumentResponse): DocNode {
  const parentPath = path.substring(0, path.lastIndexOf("/")) || "/";

  return {
    id: path,
    label: path.split("/").pop()!,
    isFolder: false,
    path,
    origin: doc.origin as "local" | "remote",
    excludedFromCodeReviews: doc.excludedFromCodeReviews,
    children: undefined,
    actions: [
      {
        label: "Add sibling",
        icon: "i-hugeicons-plus-sign",
        onSelect: () => emits("add", parentPath),
      },
      {
        label: "Delete",
        icon: "i-hugeicons-delete-02",
        onSelect: () => emits("delete", path),
      },
    ],
  };
}

/** Creates a folder node with pre-computed actions (add-child only; D-2). */
function makeFolderNode(folderPath: string, _parentPath: string): DocNode {
  return {
    id: folderPath,
    label: folderPath.split("/").pop()!,
    isFolder: true,
    defaultExpanded: true,
    path: folderPath,
    children: {} as unknown as DocNode[],
    // D-2: folder delete hidden — only add-child.
    actions: [
      {
        label: "Add document",
        icon: "i-hugeicons-plus-sign",
        onSelect: () => emits("add", folderPath),
      },
    ],
  };
}

/** Converts Record<string, DocNode> tree to sorted DocNode[]. */
function flattenRecordTree(obj: Record<string, DocNode>): DocNode[] {
  return Object.values(obj)
    .map((n) => {
      if (n.children) {
        return {
          ...n,
          children: flattenRecordTree(
            n.children as unknown as Record<string, DocNode>,
          ),
        };
      }
      return n;
    })
    .sort((a, b) => {
      // Folders before leaves, then alphabetical.
      if (a.isFolder !== b.isFolder) return a.isFolder ? -1 : 1;
      return a.label.localeCompare(b.label);
    });
}

function getNodeKey(node: DocNode) {
  return node.id;
}

function findNodeByPath(nodes: DocNode[], path: string): DocNode | null {
  for (const node of nodes) {
    if (node.path === path) {
      return node;
    }

    if (node.children) {
      const found = findNodeByPath(node.children, path);
      if (found) return found;
    }
  }

  return null;
}

function collectDefaultExpandedNodeIds(nodes: DocNode[]): string[] {
  return nodes.flatMap((node) => [
    ...(node.defaultExpanded ? [node.id] : []),
    ...(node.children ? collectDefaultExpandedNodeIds(node.children) : []),
  ]);
}

function getAncestorFolderPaths(path: string) {
  const parts = path.split("/").filter(Boolean);
  return parts.slice(0, -1).map((_, index) => {
    return "/" + parts.slice(0, index + 1).join("/");
  });
}

// ── Event handlers (thin: just decode action metadata and emit) ─────────

/** UTree @select: leaf click opens the document. */
function onSelectNode(node: DocNode) {
  if (node.isFolder) {
    emits("folderSelect", node.path);
    return;
  }

  emits("select", node.path);
}
</script>
