import type {
  CommandPaletteGroup,
  CommandPaletteItem,
  NavigationMenuItem,
} from "@nuxt/ui";

type AppNavigationItem = {
  label: string;
  icon?: string;
  to: string;
  defaultOpen?: boolean;
  onSelect?: () => void;
  children?: NavigationMenuItem[];
};

type AppNavigationSection = {
  id: string;
  label: string;
  icon?: string;
  defaultOpen?: boolean;
  items: AppNavigationItem[];
};

export type AppNavigationLibrary = {
  name: string;
  description?: string | null;
};

const mainSection: AppNavigationSection = {
  id: "main",
  label: "Main",
  items: [
    {
      label: "Home",
      icon: "i-hugeicons-home-01",
      to: "/",
    },
    {
      label: "Libraries",
      icon: "i-hugeicons-hierarchy-files",
      to: "/libraries",
    },
  ],
};

const codeReviewSection: AppNavigationSection = {
  id: "code-reviews",
  label: "Code Reviews",
  icon: "i-hugeicons-message-programming",
  defaultOpen: true,
  items: [
    {
      label: "PR Code Reviews",
      to: "/code-reviews/pull-requests",
    },
    {
      label: "Manage Agents",
      to: "/code-reviews/manage-agents",
    },
  ],
};

const settingsSection: AppNavigationSection = {
  id: "settings",
  label: "Settings",
  icon: "i-hugeicons-settings-01",
  items: [
    {
      label: "Organization",
      to: "/settings/organization",
    },
    {
      label: "Members",
      to: "/settings/members",
    },
    {
      label: "My Memberships",
      to: "/settings/memberships",
    },
    {
      label: "Credentials",
      to: "/settings/credentials",
    },
    {
      label: "GitHub",
      to: "/settings/github",
    },
    {
      label: "LLM Configuration",
      to: "/settings/llm-config",
    },
  ],
};

const systemSection: AppNavigationSection = {
  id: "system",
  label: "System",
  icon: "i-hugeicons-shield-01",
  items: [
    {
      label: "Organizations",
      icon: "i-hugeicons-building-03",
      to: "/system/organizations",
    },
    {
      label: "Diagnostics",
      icon: "i-hugeicons-activity-02",
      to: "/system/diagnostics",
    },
  ],
};

function getAppNavigationSections(isSystemAdmin: boolean) {
  const sections = [mainSection, codeReviewSection, settingsSection];

  if (isSystemAdmin) {
    sections.push(systemSection);
  }

  return sections;
}

export function buildAppNavigationLinks(
  isSystemAdmin: boolean,
  onSelect: () => void,
  libraries: AppNavigationLibrary[] = [],
): NavigationMenuItem[][] {
  const sections = getAppNavigationSections(isSystemAdmin);
  const mainItems = [
    mainSection.items[0],
    toLibrariesNavigationItem(libraries, onSelect),
  ];
  const mainLinks: NavigationMenuItem[] = [
    ...mainItems.map((item) => toNavigationItem(item, onSelect)),
    ...sections
      .filter((section) => section.id !== mainSection.id)
      .map((section) => toNavigationSection(section, onSelect)),
  ];

  return [mainLinks, []];
}

export function buildAppCommandGroups(
  isSystemAdmin: boolean,
): CommandPaletteGroup<CommandPaletteItem>[] {
  return getAppNavigationSections(isSystemAdmin).map((section) => ({
    id: section.id,
    label: section.label,
    items: section.items.map((item) => ({
      label: item.label,
      icon: item.icon ?? section.icon,
      to: item.to,
      exact: item.to === "/",
    })),
  }));
}

function toNavigationSection(
  section: AppNavigationSection,
  onSelect: () => void,
): NavigationMenuItem {
  return {
    label: section.label,
    icon: section.icon,
    defaultOpen: section.defaultOpen ?? false,
    type: "trigger",
    children: section.items.map((item) => toNavigationItem(item, onSelect)),
  };
}

function toNavigationItem(
  item: AppNavigationItem,
  onSelect: () => void,
): NavigationMenuItem {
  return {
    label: item.label,
    icon: item.icon,
    to: item.to,
    exact: item.to === "/",
    defaultOpen: item.defaultOpen,
    onSelect: item.onSelect ?? onSelect,
    children: item.children,
  };
}

function toLibrariesNavigationItem(
  libraries: AppNavigationLibrary[],
  onSelect: () => void,
): AppNavigationItem {
  return {
    label: "Libraries",
    icon: "i-hugeicons-hierarchy-files",
    to: "/libraries",
    defaultOpen: true,
    children: libraries.map((library) => ({
      label: library.name,
      description: library.description ?? undefined,
      to: libraryRoute(library.name),
      exact: true,
      onSelect,
    })),
  };
}

function libraryRoute(name: string) {
  return `/libraries/${encodeURIComponent(name)}`;
}
