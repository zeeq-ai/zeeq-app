/** Library source `repoUrl` values are normalized server-side to always end in `.git`
 * (see LibraryEndpoints.Handler.CreateLibrary.NormalizeGitHubUrl); strip that suffix
 * to get a browsable GitHub URL. */
export function toGitHubWebUrl(repoUrl: string): string {
  return repoUrl.replace(/\/?(\.git)?\/?$/i, "");
}
