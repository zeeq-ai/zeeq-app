import AppLayout from "@/layouts/AppLayout.vue";
import type { RouteRecordRaw } from "vue-router";

/**
 * Application routes.
 *
 * Login and Logout are kept under the app layout so the shell remains stable
 * while the router guard handles auth side effects.
 */
export const routes: Array<RouteRecordRaw> = [
  {
    path: "/",
    component: AppLayout,
    children: [
      {
        path: "",
        name: "Home",
        component: () => import("@/views/home/Home.vue"),
        meta: { title: "Home" },
      },
      {
        path: "document-partitions",
        redirect: "/libraries",
      },
      {
        path: "libraries/:libraryName?",
        name: "Libraries",
        component: () => import("@/views/libraries/Libraries.vue"),
        meta: { title: "Libraries" },
      },
      {
        path: "memories",
        name: "Memories",
        component: () => import("@/views/memories/Memories.vue"),
        meta: { title: "Memories" },
      },
      {
        path: "code-reviews",
        component: () => import("@/views/code-reviews/CodeReviews.vue"),
        redirect: "/code-reviews/pull-requests",
        meta: { title: "Code Reviews" },
        children: [
          {
            path: "pull-requests",
            name: "CodeReviewPullRequests",
            component: () =>
              import("@/views/code-reviews/pull-requests/PullRequestReviews.vue"),
            meta: { title: "Code Reviews" },
            // Used to support deep linking into the PR inbox.
            // prId: pre-selects a loaded inbox PR by record id.
            // reviewId: expands a specific review accordion item.
            // prNumber + repositoryId: Mode 2 — resolves a PR by provider number (repo-scoped).
            props: (route) => ({
              prId: (route.query.prId as string) || undefined,
              reviewId: (route.query.reviewId as string) || undefined,
              prNumber: (route.query.prNumber as string) || undefined,
              repositoryId: (route.query.repositoryId as string) || undefined,
            }),
          },
          {
            path: "manage-repositories",
            redirect: "/settings/github",
          },
          {
            path: "manage-agents",
            name: "ManageAgents",
            component: () =>
              import("@/views/code-reviews/manage-agents/ManageAgents.vue"),
            meta: { title: "Code Reviews" },
          },
          {
            path: "manage-agents/:orgId/agents/:agentId",
            name: "ManageAgent",
            component: () =>
              import("@/views/code-reviews/manage-agents/ManageAgents.vue"),
            meta: { title: "Code Reviews" },
            props: (route) => ({
              orgId: (route.params.orgId as string) || undefined,
              agentId: (route.params.agentId as string) || undefined,
            }),
          },
          {
            path: "reviews/:reviewId",
            name: "CodeReviewSingle",
            component: () =>
              import("@/views/code-reviews/single/SingleCodeReview.vue"),
            meta: { title: "Code Review" },
            // NOTE: (review finding — token intentionally not in the path) The `c` token is
            // mandatory-by-construction: every deep link is emitted by the backend
            // `BuildSingleReviewLink`, which always appends `?c=`. A bare `/reviews/:reviewId`
            // is handled gracefully by the view's `!token` guard (invalid-link empty state),
            // so it does not need to be encoded as a required path segment.
            props: (route) => ({
              reviewId: route.params.reviewId as string,
              token: (route.query.c as string) || undefined,
            }),
          },
          {
            // Mode 1 standalone PR view — loads one PR + review history directly from
            // (pullRequestRecordId, c), no inbox required.
            // URL: /code-reviews/pull-requests/:pullRequestRecordId/single?c=<token>
            // NOTE: `c` replaced the earlier `?createdAtUtc=<iso>` shape. The token is
            // minted server-side (CodeReviewSingleViewToken) to avoid JS Date microsecond
            // truncation that caused partition lookup 404s.
            path: "pull-requests/:pullRequestRecordId/single",
            name: "CodeReviewPullRequestSingle",
            component: () =>
              import("@/views/code-reviews/single/SinglePullRequestView.vue"),
            meta: { title: "Pull Request" },
            props: (route) => ({
              pullRequestRecordId: route.params.pullRequestRecordId as string,
              c: (route.query.c as string) || undefined,
            }),
          },
        ],
      },
      {
        path: "telemetry",
        component: () => import("@/views/telemetry/Telemetry.vue"),
        redirect: "/telemetry/my-conversations",
        meta: { title: "Telemetry" },
        children: [
          {
            path: "my-conversations",
            name: "MyConversations",
            component: () => import("@/views/telemetry/MyConversations.vue"),
            meta: { title: "Telemetry" },
          },
        ],
      },
      {
        path: "system",
        component: () => import("@/views/system/System.vue"),
        redirect: "/system/diagnostics",
        meta: { title: "System", requiresSystemAdmin: true },
        children: [
          {
            path: "diagnostics",
            name: "SystemDiagnostics",
            component: () =>
              import("@/views/system/diagnostics/Diagnostics.vue"),
            meta: { title: "Diagnostics", requiresSystemAdmin: true },
          },
          {
            path: "organizations",
            name: "SystemOrganizations",
            component: () =>
              import("@/views/system/organizations/SystemOrganizations.vue"),
            meta: { title: "Organizations", requiresSystemAdmin: true },
          },
        ],
      },
      {
        path: "settings",
        component: () => import("@/views/settings/Settings.vue"),
        redirect: "/settings/organization",
        meta: { title: "Settings" },
        children: [
          {
            path: "organization",
            name: "SettingsOrganization",
            component: () => import("@/views/settings/Organization.vue"),
            meta: { title: "Organization" },
          },
          {
            path: "members",
            name: "SettingsMembers",
            component: () => import("@/views/settings/Members.vue"),
            meta: { title: "Members" },
          },
          {
            path: "memberships",
            name: "SettingsMyMemberships",
            component: () => import("@/views/settings/MyMemberships.vue"),
            meta: { title: "My Memberships" },
          },
          {
            path: "credentials",
            name: "SettingsCredentials",
            component: () =>
              import("@/views/settings/credentials/Credentials.vue"),
            meta: { title: "Credentials" },
          },
          {
            path: "github",
            name: "SettingsGitHub",
            component: () =>
              import("@/views/settings/manage-github/ManageGitHub.vue"),
            meta: { title: "GitHub" },
          },
          {
            path: "llm-config",
            name: "SettingsLlmConfig",
            component: () =>
              import("@/views/settings/llm-config/LlmConfig.vue"),
            meta: { title: "LLM Configuration" },
          },
        ],
      },
    ],
  },
  {
    path: "/login",
    component: () => import("@/views/login/Login.vue"),
  },
  {
    path: "/auth/login",
    component: () => import("@/views/login/Login.vue"),
  },
  {
    // DCR org picker — bare route (no AppLayout). Shown when an authenticated
    // user with more than one active org starts /connect/authorize.
    path: "/select-org",
    name: "SelectOrg",
    component: () => import("@/views/login/SelectOrg.vue"),
  },
  {
    path: "/join-your-team",
    name: "JoinYourTeam",
    component: () => import("@/views/join-your-team/JoinYourTeam.vue"),
  },
  {
    path: "/activate-account",
    redirect: "/login?inactiveOrg=true",
  },
  {
    path: "/activate-organization",
    redirect: "/login?inactiveOrg=true",
  },
  {
    path: "/auth/complete/:provider",
    name: "AuthComplete",
    component: () => import("@/views/login/AuthComplete.vue"),
  },
];
