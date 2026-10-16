// Regression contract for the pinned http-cache-semantics source patch.
// Run from repository root: node --test .yarn/patches/http-cache-semantics.test.cjs
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { createRequire } = require('node:module');
const { join } = require('node:path');
const CachePolicy = createRequire(join(process.cwd(), 'package.json'))('http-cache-semantics');
const request = { method: 'GET', url: 'https://cache.test/resource', headers: { host: 'cache.test', accept: 'text/plain' } };
const staleRequest = { ...request, headers: { ...request.headers, 'cache-control': 'max-stale=600' } };
function policy(cacheControl, { shared = false, age = 120, headers = {}, storedRequest = request } = {}) {
  const result = new CachePolicy(storedRequest, { status: 200, headers: { 'cache-control': cacheControl, etag: '"v1"', ...headers } }, { shared });
  const created = result.now();
  result.now = () => created + age * 1000;
  return result;
}
for (const shared of [false, true]) {
  test(`no-cache blocks max-stale and stale extensions (shared=${shared})`, () => {
    const p = policy('no-cache, stale-while-revalidate=600, stale-if-error=600', { shared });
    assert.equal(p.satisfiesWithoutRevalidation(staleRequest), false);
    assert.equal(p.useStaleWhileRevalidate(), false);
    assert.equal(p.revalidatedPolicy(request, { status: 503, headers: {} }).modified, true);
  });
}
test('shared-cache prohibitions override max-stale', () => {
  for (const cc of ['private', 'no-store', 'max-age=60, must-revalidate', 'max-age=60, proxy-revalidate', 's-maxage=60']) {
    assert.equal(policy(cc, { shared: true }).satisfiesWithoutRevalidation(staleRequest), false, cc);
  }
  assert.equal(policy('max-age=60', { shared: true, headers: { 'set-cookie': 'session=alice' } }).satisfiesWithoutRevalidation(staleRequest), false);
});
test('error fallback respects URL, Host, and Vary', () => {
  for (const shared of [false, true]) {
    const p = policy('max-age=60, stale-if-error=600', { shared, headers: { vary: 'accept' } });
    assert.notEqual(p.evaluateRequest(staleRequest).response, undefined);
    for (const req of [
      { ...request, url: 'https://cache.test/other' },
      { ...request, headers: { ...request.headers, host: 'other.test' } },
      { ...request, headers: { ...request.headers, accept: 'application/json' } },
    ]) {
      const allowsStale = { ...req, headers: { ...req.headers, 'cache-control': 'max-stale=600' } };
      assert.equal(p.evaluateRequest(allowsStale).response, undefined);
      assert.equal(p.revalidatedPolicy(req, { status: 503, headers: {} }).modified, true);
    }
  }
});
test('HEAD error fallback never reuses a stored POST', () => {
  const p = policy('max-age=60, stale-if-error=600', { storedRequest: { ...request, method: 'POST' } });
  assert.equal(p.revalidatedPolicy({ ...request, method: 'HEAD' }, { status: 503, headers: {} }).modified, true);
});
test('normal fresh cache hits honor Vary', () => {
  const p = policy('max-age=60', { age: 30, headers: { vary: 'accept' } });
  assert.equal(p.satisfiesWithoutRevalidation(request), true);
  assert.equal(p.satisfiesWithoutRevalidation({ ...request, headers: { ...request.headers, accept: 'application/json' } }), false);
});
test('fresh must-revalidate remains reusable; stale requires validation', () => {
  assert.equal(policy('max-age=60, must-revalidate', { age: 30 }).satisfiesWithoutRevalidation(request), true);
  assert.equal(policy('max-age=60, must-revalidate').satisfiesWithoutRevalidation(staleRequest), false);
});
test('ordinary max-stale is allowed through its inclusive boundary', () => {
  const p = policy('max-age=60');
  assert.equal(p.satisfiesWithoutRevalidation(request), false);
  assert.equal(p.satisfiesWithoutRevalidation({ ...request, headers: { ...request.headers, 'cache-control': 'max-stale=60' } }), true);
  assert.equal(p.satisfiesWithoutRevalidation({ ...request, headers: { ...request.headers, 'cache-control': 'max-stale=59' } }), false);
});
test('permitted stale extensions retain their behavior', () => {
  // NOTE: Expiry alone permits stale-if-error; directive prohibitions are tested separately.
  for (const shared of [false, true]) {
    const p = policy('max-age=60, stale-while-revalidate=600, stale-if-error=600', { shared, headers: { vary: 'accept' } });
    assert.equal(p.stale(), true);
    assert.equal(p.evaluateRequest(request).revalidation.synchronous, false);
    assert.equal(p.revalidatedPolicy(request, { status: 503, headers: {} }).modified, false);
    const conditional = { ...request, headers: p.revalidationHeaders(request) };
    assert.equal(conditional.headers['if-none-match'], '"v1"');
    const fallback = p.revalidatedPolicy(conditional, { status: 503, headers: {} });
    assert.equal(fallback.modified, false);
    assert.equal(fallback.matches, true);
    assert.equal(fallback.policy, p);
  }
});
test('ETag revalidation and successful 304 retain the cached body', () => {
  const p = policy('max-age=60');
  assert.equal(p.revalidationHeaders(request)['if-none-match'], '"v1"');
  const updated = p.revalidatedPolicy(request, { status: 304, headers: { etag: '"v1"', 'cache-control': 'max-age=300' } });
  assert.equal(updated.modified, false);
  assert.equal(updated.policy.satisfiesWithoutRevalidation(request), true);
});
test('serialized policies retain the no-cache guard', () => {
  const p = policy('no-cache');
  const restored = CachePolicy.fromObject(p.toObject());
  restored.now = p.now;
  assert.equal(restored.satisfiesWithoutRevalidation(staleRequest), false);
});
test('private cache retains cookie and proxy-revalidate max-stale behavior', () => {
  assert.equal(policy('max-age=60', { headers: { 'set-cookie': 'session=alice' } }).satisfiesWithoutRevalidation(staleRequest), true);
  assert.equal(policy('max-age=60, proxy-revalidate').satisfiesWithoutRevalidation(staleRequest), true);
});
