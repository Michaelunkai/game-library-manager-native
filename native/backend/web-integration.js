// Added before the website's existing DOMContentLoaded initialization.
(function installConditionalSync(Prototype) {
  const legacyLoad = Prototype.loadAdminConfigFromServer;
  const legacySave = Prototype.saveAdminConfigToServer;
  const legacyPolling = Prototype.startAdminConfigPolling;
  const clone = value => JSON.parse(JSON.stringify(value));
  const capture = app => ({ hiddenTabs: [...app.hiddenTabs].sort(), tabs: app.normalizeTabs(app.tabs), gameCategories: Object.fromEntries(app.games.filter(g => g.category).map(g => [g.id, g.category])) });
  const controller = app => app._conditionalAdminSync || (app._conditionalAdminSync = new window.ConditionalAdminSync(localStorage));
  function apply(app, config) {
    app.hiddenTabs = new Set(config.hiddenTabs || []);
    if (Array.isArray(config.tabs)) app.tabs = app.normalizeTabs(config.tabs);
    for (const game of app.games) if (config.gameCategories[game.id]) game.category = config.gameCategories[game.id];
    app.serverGameCategories = clone(config.gameCategories);
    localStorage.setItem('hiddenTabs', JSON.stringify([...app.hiddenTabs]));
    localStorage.setItem('gameLibraryTabs', JSON.stringify(app.tabs));
    localStorage.setItem('gameLibraryGames', JSON.stringify(app.games));
    app._lastAdminConfigSignature = app.getAdminConfigSignature(config);
    app.renderTabs(); app.filterAndRender();
  }
  function banner(app) {
    let box = document.getElementById('sharedSyncNotice');
    if (!box) {
      box = document.createElement('div'); box.id = 'sharedSyncNotice'; box.setAttribute('role', 'status');
      Object.assign(box.style, { position: 'fixed', bottom: '18px', left: '18px', zIndex: '10000', maxWidth: '640px', padding: '16px', borderRadius: '12px', background: '#20302b', color: '#fff', boxShadow: '0 8px 32px #0006' });
      document.body.appendChild(box);
    }
    box.replaceChildren();
    const sync = controller(app), conflicts = sync.pending.filter(e => e.conflict);
    box.hidden = sync.pending.length === 0;
    if (box.hidden) return;
    const message = document.createElement('div');
    message.textContent = conflicts.length ? 'Shared changes conflict: ' + conflicts.map(e => e.key || e.section).join(', ') + '. Your edits are saved on this device.' : sync.pending.length + ' shared change(s) saved on this device, awaiting server confirmation.';
    box.appendChild(message);
    function button(label, action) { const node = document.createElement('button'); node.textContent = label; node.style.margin = '10px 8px 0 0'; node.addEventListener('click', action); box.appendChild(node); }
    button('Retry sync', () => app.saveAdminConfigToServer());
    if (conflicts.length) {
      button('Review differences', () => {
        const detail = document.createElement('pre'); detail.style.whiteSpace = 'pre-wrap'; detail.style.maxHeight = '240px'; detail.style.overflow = 'auto';
        detail.textContent = conflicts.map(e => (e.key || e.section) + '\nYour edit: ' + JSON.stringify(e.after) + '\nServer: ' + JSON.stringify(e.section === 'gameCategories' ? sync.remote.gameCategories[e.key] : sync.remote[e.section])).join('\n\n'); box.appendChild(detail);
      });
      button('Publish my reviewed edits', () => { apply(app, sync.resolve(true)); app.saveAdminConfigToServer(); });
      button('Use server values', () => { apply(app, sync.resolve(false)); banner(app); });
    }
  }
  async function read(app) {
    const response = await fetch('/api/admin-config?t=' + Date.now(), { cache: 'no-store', signal: AbortSignal.timeout(20000) });
    if (!response.ok) throw Error('Server read failed: ' + response.status);
    const data = await response.json();
    if (!data.success || !data.config || !data.configVersion) throw Error('Server did not provide a valid configuration version.');
    return data;
  }
  Prototype.loadAdminConfigFromServer = async function () {
    try {
      const epoch = this._sharedEpoch || 0;
      const sync = controller(this), data = await read(this);
      if (this._sharedSavePromise || (this._sharedEpoch || 0) !== epoch) return;
      if (data.capabilities?.conditionalWrites !== true && this._conditionalMode !== true) {
        this._conditionalMode = false;
        return await legacyLoad.call(this);
      }
      this._conditionalMode = true;
      apply(this, sync.receive(data.config, data.configVersion, capture(this)));
      this._lastConfigVersion = data.configVersion; banner(this);
    } catch (error) {
      console.warn('Shared configuration preserved:', error.message);
      // The last durable local state remains usable while offline.
      if (this._conditionalMode !== true) await legacyLoad.call(this);
    }
  };
  Prototype.startAdminConfigPolling = function () {
    if (this._conditionalMode !== true) return legacyPolling.call(this);
    clearInterval(this.configPollInterval);
    this.configPollInterval = setInterval(async () => {
      if (this._sharedSavePromise || this._sharedPolling) return;
      this._sharedPolling = true;
      try {
        if (this.isAdmin && controller(this).pending.length) await this.saveAdminConfigToServer();
        else await this.loadAdminConfigFromServer();
      } finally { this._sharedPolling = false; }
    }, 2000);
  };
  Prototype.saveAdminConfigToServer = function () {
    if (this._conditionalMode !== true) return legacySave.call(this);
    if (!this.isAdmin) return Promise.resolve({ success: false, error: 'Admin sign in is required.' });
    let sync;
    try { sync = controller(this); sync.queue(capture(this)); banner(this); }
    catch (error) { this.showToast(error.message, 'error'); return Promise.resolve({ success: false, error: error.message }); }
    if (this._sharedSavePromise) return this._sharedSavePromise;
    this._sharedEpoch = (this._sharedEpoch || 0) + 1;
    this._sharedSavePromise = (async () => {
      try {
        for (let attempt = 0; attempt < 4 && sync.pending.length; attempt++) {
          const latest = await read(this);
          apply(this, sync.receive(latest.config, latest.configVersion, capture(this)));
          if (!sync.pending.length) break;
          const payload = sync.snapshot(), sent = clone(sync.pending);
          const response = await fetch('/api/admin-config', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Admin-Token': 'glm-admin-2024', 'If-Match': payload.expectedVersion }, body: JSON.stringify(payload), signal: AbortSignal.timeout(20000) });
          if (response.status === 409 || response.status === 412) continue;
          if (!response.ok) throw Error('Save failed: HTTP ' + response.status + '. Your edits remain queued.');
          const saved = await response.json();
          if (!saved.success || !saved.configVersion) throw Error('The server did not confirm the write. Your edits remain queued.');
          const confirmed = await read(this);
          sync.acknowledge(sent, confirmed.config);
          apply(this, sync.receive(confirmed.config, confirmed.configVersion, capture(this)));
        }
        if (sync.pending.length) throw Error('Some edits need another sync or conflict review.');
        this.showToast('Shared changes confirmed by the server.', 'success');
        return { success: true };
      } catch (error) {
        this.showToast(error.message, 'error');
        return { success: false, error: error.message };
      } finally { banner(this); }
    })().finally(() => { this._sharedSavePromise = null; });
    return this._sharedSavePromise;
  };
})(GameLibrary.prototype);
