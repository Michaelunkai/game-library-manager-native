const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const Sync = require('./admin-sync');
const copy = v => JSON.parse(JSON.stringify(v));
const storage = () => { const entries = new Map(); return { getItem: key => entries.get(key) || null, setItem: (key, value) => entries.set(key, value) }; };
const base = () => ({ gameCategories: { a: 'old', b: 'old', A: 'case-distinct' }, hiddenTabs: [], tabs: [{ id: 'old', name: 'Old' }] });
const checks = [];
async function check(name, action) { await action(); checks.push({ name, passed: true }); }
(async () => {
  await check('Disjoint website and native edits merge without reverting remote fields', () => { const s = new Sync(storage()); let view = s.receive(base(), 'v1'); view.gameCategories.a = 'browser'; s.queue(view); const remote = base(); remote.gameCategories.b = 'native'; s.receive(remote, 'v2'); assert.equal(s.snapshot().gameCategories.b, 'native'); assert.equal(s.snapshot().gameCategories.a, 'browser'); });
  await check('Same-field conflict preserves both values and blocks publishing', () => { const s = new Sync(storage()); let view = s.receive(base(), 'v1'); view.gameCategories.a = 'browser'; s.queue(view); const remote = base(); remote.gameCategories.a = 'native'; assert.equal(s.receive(remote, 'v2').gameCategories.a, 'browser'); assert.equal(s.remote.gameCategories.a, 'native'); assert.throws(() => s.snapshot()); });
  await check('Offline outbox survives recreation and reconnect', () => { const data = storage(); const s = new Sync(data); let view = s.receive(base(), 'v1'); view.hiddenTabs = ['old']; s.queue(view); const restored = new Sync(data); restored.receive(base(), 'v1'); assert.deepEqual(restored.snapshot().hiddenTabs, ['old']); });
  await check('Uncertain acknowledged POST is reconciled without repeating a mutation', () => { const s = new Sync(storage()); let view = s.receive(base(), 'v1'); view.gameCategories.a = 'saved'; s.queue(view); s.receive(view, 'v2'); assert.equal(s.pending.length, 0); });
  await check('Edits arriving during an HTTP await retain their own pending state', () => { const s = new Sync(storage()); let view = s.receive(base(), 'v1'); view.gameCategories.a = 'first'; s.queue(view); const sent = copy(s.pending), saved = s.snapshot(); view.gameCategories.a = 'second'; s.queue(view); s.acknowledge(sent, saved); s.receive(saved, 'v2'); assert.equal(s.pending.length, 1); assert.equal(s.snapshot().gameCategories.a, 'second'); });
  await check('Explicit conflict resolution rebases reviewed local edits', () => { const s = new Sync(storage()); let view = s.receive(base(), 'v1'); view.gameCategories.a = 'browser'; s.queue(view); const remote = base(); remote.gameCategories.a = 'native'; s.receive(remote, 'v2'); s.resolve(true); assert.equal(s.snapshot().expectedVersion, 'v2'); assert.equal(s.snapshot().gameCategories.a, 'browser'); });
  await check('Case-distinct game identities remain independent', () => { const s = new Sync(storage()); const view = s.receive(base(), 'v1'); view.gameCategories.a = 'changed'; s.queue(view); assert.equal(s.snapshot().gameCategories.A, 'case-distinct'); });
  await check('Corrupt persisted queue is rejected without overwriting it', () => { const data = storage(); data.setItem('gameLibraryAdminOutboxV1', '{broken'); assert.throws(() => new Sync(data)); assert.equal(data.getItem('gameLibraryAdminOutboxV1'), '{broken'); });
  await check('Integrated website queues, sends version, and reconciles failed acknowledgements', async () => {
    const data = storage(); let remote = base(), version = 'v1', posts = 0, failResponse = true;
    class GameLibrary {}
    function element() { return { style: {}, appendChild() {}, replaceChildren() {}, setAttribute() {}, addEventListener() {} }; }
    const context = { GameLibrary, window: { ConditionalAdminSync: Sync }, localStorage: data, document: { getElementById: () => element(), createElement: element, body: element() }, AbortSignal, console, setInterval, clearInterval, fetch: async (url, options = {}) => {
      if (options.method === 'POST') {
        posts++; const payload = JSON.parse(options.body); assert.equal(payload.expectedVersion, version); assert.equal(options.headers['If-Match'], version);
        remote = payload; version = 'v2';
        if (failResponse) throw Error('transport interrupted after commit');
        return { ok: true, status: 200, json: async () => ({ success: true, configVersion: version }) };
      }
      return { ok: true, status: 200, json: async () => ({ success: true, config: copy(remote), configVersion: version, capabilities: { conditionalWrites: true } }) };
    } };
    vm.runInNewContext(fs.readFileSync(path.join(__dirname, 'web-integration.js'), 'utf8'), context);
    const app = new GameLibrary(); Object.assign(app, { isAdmin: true, hiddenTabs: new Set(), tabs: remote.tabs, games: [{ id: 'a', category: 'old' }, { id: 'b', category: 'old' }], normalizeTabs: x => x, getAdminConfigSignature: JSON.stringify, renderTabs() {}, filterAndRender() {}, showToast() {} });
    await app.loadAdminConfigFromServer(); app.games[0].category = 'browser';
    assert.equal((await app.saveAdminConfigToServer()).success, false); assert.equal(app._conditionalAdminSync.pending.length, 1);
    failResponse = false; assert.equal((await app.saveAdminConfigToServer()).success, true); assert.equal(posts, 1); assert.equal(app._conditionalAdminSync.pending.length, 0);
  });
  await check('Other hosting and local backends retain their existing write and fallback paths', async () => {
    const calls = [];
    class GameLibrary { async loadAdminConfigFromServer() { calls.push('load'); } saveAdminConfigToServer() { calls.push('save'); } startAdminConfigPolling() { calls.push('poll'); } }
    const context = { GameLibrary, window: { ConditionalAdminSync: Sync }, localStorage: storage(), AbortSignal, console, fetch: async () => ({ ok: true, json: async () => ({ success: true, config: base(), configVersion: 'legacy' }) }) };
    vm.runInNewContext(fs.readFileSync(path.join(__dirname, 'web-integration.js'), 'utf8'), context);
    const app = new GameLibrary(); await app.loadAdminConfigFromServer(); app.saveAdminConfigToServer(); app.startAdminConfigPolling();
    assert.deepEqual(calls, ['load', 'save', 'poll']);
  });
  fs.writeFileSync(path.join(__dirname, '../evidence/browser-sync-logic.json'), JSON.stringify({ at: new Date().toISOString(), passed: true, actualBrowserUI: false, productionDeployed: false, checks }, null, 2));
  console.log('PASS: ' + checks.length + ' browser queue/integration checks. Actual Chrome UI proof is separate.');
})().catch(error => { console.error(error); process.exitCode = 1; });
