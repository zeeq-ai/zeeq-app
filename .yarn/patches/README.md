# Dependency patches

`http-cache-semantics-npm-4.3.0-ee45f76976.patch` applies the source changes from
[upstream PR #63](https://github.com/kornelski/http-cache-semantics/pull/63), pinned
to commit `497f230aaefc2013daa1d9785a6c6c34d08f6c75`, to registry version 4.3.0.
That release alone does not fix the stale-response reuse defect described by
GHSA-ch52-4w7c-c8xp. The patch enforces revalidation and request identity checks.

The upstream PR is unmerged. It also permits fresh `must-revalidate` responses
to be reused, including authenticated responses explicitly allowed in shared
caches unless `Vary: Authorization` distinguishes them. The current dependency
path through `make-fetch-happen` uses a private cache (`shared: false`).
Its separate transport-error fallback bypasses this policy; this patch does not
change that parent behavior.

NOTE: Ordinary expiry does not prohibit reuse: `stale-if-error` still permits a
matching request to reuse an expired response after a 503, including conditional
requests carrying validators. The guard rejects reuse prohibited by `no-cache`
and the other response directives covered by the regressions. Both private and
shared cache modes are tested.

NOTE: Yarn's generated `#~/.yarn/patches/` locator is relative to the project
root, not the user's home directory. Preserve the generated locator and patch
bytes so immutable installs, including Docker installs, use the same source.

Run `node --test .yarn/patches/http-cache-semantics.test.cjs` from the repository
root to verify the installed patch. The Docker dependency stage runs these
regressions after its immutable install.

Remove the patch and its root resolution once an upstream release includes the
fix, after rerunning the cache policy regression checks. Keep this directory in
the Docker dependency-install stage so immutable installs apply the same patch.
