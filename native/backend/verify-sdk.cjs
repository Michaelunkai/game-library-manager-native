const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { getStore } = require('@netlify/blobs');
const { createSafeFetch } = require('./safe-fetch');
const calls = [], checks = [];
let status = 200, etag = 'wire-etag';
const transport = async (url, init) => {
  const headers = Object.fromEntries(new Headers(init.headers));
  calls.push({ method: init.method.toUpperCase(), url: String(url), headers: Object.fromEntries(Object.entries(headers).filter(([k]) => k !== 'authorization')), payload: init.body !== undefined });
  if (init.body === undefined) return new Response(JSON.stringify({ url: 'https://blobs.fixture.test/signed-put' }), { status: 200, headers: { 'content-type': 'application/json' } });
  return new Response('', { status, headers: etag ? { etag } : {} });
};
const store = getStore({ name: 'game-library-admin-config', siteID: 'fixture', token: 'fixture-token', consistency: 'strong', fetch: createSafeFetch(transport) });
async function check(name, action) { await action(); checks.push({ name, passed: true }); }
(async () => {
  await check('Actual pinned SDK transmits If-Match for setJSON', async () => { const r = await store.setJSON('admin-config', { value: 1 }, { onlyIfMatch: 'observed-etag' }); assert.equal(r.modified, true); assert.equal(r.etag, 'wire-etag'); assert.equal(calls.find(c => c.payload).headers['if-match'], 'observed-etag'); });
  await check('Actual pinned SDK transmits If-None-Match:* for creation', async () => { calls.length = 0; await store.setJSON('admin-config', { value: 2 }, { onlyIfNew: true }); assert.equal(calls.find(c => c.payload).headers['if-none-match'], '*'); });
  await check('Actual SDK rejects a provider precondition failure', async () => { status = 412; etag = null; assert.equal((await store.setJSON('admin-config', {}, { onlyIfMatch: 'stale' })).modified, false); });
  await check('HTTP 503 cannot become a false successful SDK write', async () => { status = 503; etag = 'even-an-error-etag'; await assert.rejects(store.setJSON('admin-config', {}, { onlyIfMatch: 'expected' })); });
  await check('HTTP 401 cannot become a false successful SDK write', async () => { status = 401; await assert.rejects(store.setJSON('admin-config', {}, { onlyIfMatch: 'expected' })); });
  await check('Success without ETag cannot acknowledge a write', async () => { status = 200; etag = null; await assert.rejects(store.setJSON('admin-config', {}, { onlyIfMatch: 'expected' })); });
  fs.writeFileSync(path.join(__dirname, '../evidence/backend-sdk.json'), JSON.stringify({ at: new Date().toISOString(), passed: true, sdkVersion: JSON.parse(fs.readFileSync(path.join(__dirname, 'node_modules/@netlify/blobs/package.json'))).version, liveProvider: false, checks, lastRequest: calls.at(-1) }, null, 2));
  console.log('PASS: ' + checks.length + ' actual SDK wire/error checks.');
})().catch(error => { console.error(error); process.exitCode = 1; });
