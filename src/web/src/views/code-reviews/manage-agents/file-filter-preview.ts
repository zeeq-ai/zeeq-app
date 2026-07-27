/**
 * Parses the freeform preview textarea into repository-relative paths.
 * Blank lines are ignored and duplicates collapse case-insensitively to match
 * the backend preview endpoint.
 */
export function parsePreviewFilePaths(input: string): string[] {
  const seen = new Set<string>();
  const paths: string[] = [];

  for (const line of input.split(/\r?\n/)) {
    const path = line.trim().replaceAll("\\", "/").replace(/^\/+/, "");
    const key = path.toLowerCase();

    if (!path || seen.has(key)) {
      continue;
    }

    seen.add(key);
    paths.push(path);
  }

  return paths;
}
