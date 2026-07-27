type RepositoryChangeAction = () => void | Promise<void>;
type RepositoryChangeGuard = (
  action: RepositoryChangeAction,
) => void | Promise<unknown>;

let activeGuard: RepositoryChangeGuard | null = null;

/**
 * The repository picker lives in the parent toolbar, while the dirty editor state
 * lives in ManageAgents. This module gives the child view a narrow way to guard
 * the parent's repository mutation without moving editor state into the store.
 */
export function setManageAgentsRepositoryChangeGuard(
  guard: RepositoryChangeGuard,
) {
  activeGuard = guard;
}

/** Clears only the guard registered by the current mounted ManageAgents view. */
export function clearManageAgentsRepositoryChangeGuard(
  guard: RepositoryChangeGuard,
) {
  if (activeGuard === guard) {
    activeGuard = null;
  }
}

/** Runs repository changes through the active ManageAgents unsaved-changes guard. */
export async function confirmManageAgentsRepositoryChange(
  action: RepositoryChangeAction,
) {
  if (activeGuard) {
    await activeGuard(action);
    return;
  }

  await action();
}
