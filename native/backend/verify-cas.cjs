const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, 'admin-config.js'), 'utf8');
const clone = v => JSON.parse(JSON.stringify(v));
let entry = { data: { hiddenTabs: [], gameCategories: { a: 'old', b: 'old' }, tabs: [], lastUpdated: 'old' }, etag: 'v1' };
let writes = 0, offline = false, failWrite = false, race = false;
const store = {
  async getWithMetadata() { if (offline) throw Error('offline fixture'); return clone(entry); },
  async setJSON(key, data, options) {
    writes++;
    if (failWrite) throw Error('write failed fixture');
    if (race) { race = false; entry = { data: { ...entry.data, gameCategories: { ...entry.data.gameCategories, a: 'other' } }, etag: 'raced' }; }
    if (!options || (!options.onlyIfMatch && !options.onlyIfNew)) throw Error('Unconditional write forbidden');
    if (options.onlyIfMatch !== entry?.etag && !(options.onlyIfNew && !entry)) return { modified: false };
    entry = { data: clone(data), etag: 'v' + writes + '-' + Date.now() };
    return { modified: true, etag: entry.etag };
  }
};
function instance() {
  const context = { exports: {}, process: { env: { ADMIN_TOKEN: 'fixture-admin' }, cwd: () => __dirname }, __dirname, Buffer, console: { warn() {}, error() {}, log() {} }, require(name) { if (name === '@netlify/blobs') return { getStore: () => store }; return require(name); } };
  vm.runInNewContext(source, context, { filename: 'admin-config.js' });
  return context.exports.handler;
}
const request = (version, category) => ({ httpMethod: 'POST', headers: { 'x-admin-token': 'fixture-admin' }, body: JSON.stringify({ expectedVersion: version, gameCategories: { a: category } }) });
const checks = [];
async function check(name, action) { await action(); checks.push({ name, passed: true }); }
(async () => {
  const a = instance(), b = instance();
  await check('GET exposes authoritative ETag and conditional-write capability', async () => { const r = await a({ httpMethod: 'GET' }); const d = JSON.parse(r.body); assert.equal(d.configVersion, 'v1'); assert.equal(d.capabilities.conditionalWrites, true); });
  await check('Missing version cannot silently overwrite from an old client', async () => { assert.equal((await a(request(undefined, 'bad'))).statusCode, 428); assert.equal(writes, 0); });
  await check('Unauthorized save is rejected before any storage mutation', async () => { const r = request('v1', 'bad'); r.headers = {}; assert.equal((await a(r)).statusCode, 401); assert.equal(writes, 0); });
  await check('Two independent function instances produce exactly one winner', async () => { const results = await Promise.all([a(request('v1', 'one')), b(request('v1', 'two'))]); assert.deepEqual(results.map(r => r.statusCode).sort(), [200, 409]); assert.ok(['one', 'two'].includes(entry.data.gameCategories.a)); assert.equal(entry.data.gameCategories.b, 'old'); });
  await check('A write racing after preflight is rejected atomically', async () => { race = true; assert.equal((await a(request(entry.etag, 'bad'))).statusCode, 409); assert.equal(entry.data.gameCategories.a, 'other'); });
  await check('Read failure cannot trigger stale GitHub fallback writes', async () => { const before = writes; offline = true; assert.equal((await a(request(entry.etag, 'bad'))).statusCode, 503); assert.equal(writes, before); offline = false; });
  await check('Write failure does not downgrade to unconditional persistence', async () => { failWrite = true; assert.equal((await a(request(entry.etag, 'bad'))).statusCode, 503); assert.equal(entry.data.gameCategories.a, 'other'); failWrite = false; });
  await check('Successful CAS returns the same version as authoritative read-back', async () => { const r = await a(request(entry.etag, 'confirmed')); const get = await b({ httpMethod: 'GET' }); assert.equal(r.statusCode, 200); assert.equal(JSON.parse(r.body).configVersion, JSON.parse(get.body).configVersion); assert.equal(entry.data.gameCategories.a, 'confirmed'); });
  await check('Malformed JSON and non-object config are rejected', async () => { for (const body of ['{', 'null', '[]']) assert.equal((await a({ httpMethod: 'POST', headers: { 'x-admin-token': 'fixture-admin' }, body })).statusCode, 400); });
  await check('Missing-document creation uses onlyIfNew with one independent winner', async () => { entry = null; const initial = JSON.parse((await a({ httpMethod: 'GET' })).body).configVersion; const results = await Promise.all([a(request(initial, 'created-a')), b(request(initial, 'created-b'))]); assert.deepEqual(results.map(r => r.statusCode).sort(), [200, 409]); });
  fs.writeFileSync(path.join(__dirname, '../evidence/backend-cas.json'), JSON.stringify({ at: new Date().toISOString(), passed: true, productionDeployed: false, checks }, null, 2));
  console.log('PASS: ' + checks.length + ' conditional backend checks. Not production deployment proof.');
})().catch(error => { console.error(error); process.exitCode = 1; });
