// Shared browser sync controller. The queue is persisted before every request.
// This module contains no credentials and does not contact any service by itself.
(function (root) {
  const copy = value => value === undefined ? undefined : JSON.parse(JSON.stringify(value));
  const equal = (a, b) => JSON.stringify(a) === JSON.stringify(b);
  const get = (config, edit) => edit.section === 'gameCategories' ? config.gameCategories[edit.key] : config[edit.section];
  const set = (config, edit) => { if (edit.section === 'gameCategories') config.gameCategories[edit.key] = copy(edit.after); else config[edit.section] = copy(edit.after); };
  class ConditionalAdminSync {
    constructor(storage, key = 'gameLibraryAdminOutboxV1') {
      this.storage = storage; this.key = key; this.pending = []; this.remote = null; this.displayed = null; this.version = null;
      const saved = storage.getItem(key);
      if (saved) {
        const parsed = JSON.parse(saved);
        if (!Array.isArray(parsed) || parsed.some(e => !['gameCategories', 'hiddenTabs', 'tabs'].includes(e.section))) throw Error('Invalid shared edit queue. Export local storage before recovery.');
        this.pending = parsed;
      }
    }
    persist() { this.storage.setItem(this.key, JSON.stringify(this.pending)); }
    receive(config, version, fallback) {
      this.remote = { hiddenTabs: [], tabs: copy(config.tabs ?? fallback?.tabs ?? null), ...copy(config), gameCategories: { ...(fallback?.gameCategories || {}), ...copy(config.gameCategories || {}) } };
      if (!Array.isArray(this.remote.tabs)) this.remote.tabs = copy(fallback?.tabs ?? null);
      this.version = version;
      // Reconcile an uncertain prior POST by its actual values before any retry.
      this.pending = this.pending.filter(e => !equal(get(this.remote, e), e.after));
      this.displayed = copy(this.remote);
      for (const edit of this.pending) {
        edit.conflict = !equal(get(this.remote, edit), edit.before);
        set(this.displayed, edit); // Always keep unsaved edits visible and recoverable.
      }
      this.persist();
      return copy(this.displayed);
    }
    queue(config) {
      if (!this.remote || !this.displayed) throw Error('Connect once before editing shared configuration.');
      const edits = ['hiddenTabs', 'tabs'].map(section => ({ section, key: '' }));
      for (const key of Object.keys(config.gameCategories)) edits.push({ section: 'gameCategories', key });
      for (const key of edits) {
        const next = get(config, key);
        if (equal(get(this.displayed, key), next)) continue;
        let pending = this.pending.find(e => e.section === key.section && e.key === key.key);
        if (!pending) { pending = { ...key, before: copy(get(this.remote, key)) }; this.pending.push(pending); }
        pending.after = copy(next);
        // A new edit does not silently resolve an existing conflict.
      }
      this.pending = this.pending.filter(e => !equal(e.before, e.after));
      this.displayed = copy(config); this.persist();
    }
    snapshot() {
      if (this.pending.some(e => e.conflict)) throw Error('Shared edits conflict with another client. Resolve them before publishing.');
      const payload = copy(this.remote);
      for (const edit of this.pending) set(payload, edit);
      payload.expectedVersion = this.version;
      return payload;
    }
    acknowledge(sent, config) {
      for (const saved of sent) {
        const current = this.pending.find(e => e.section === saved.section && e.key === saved.key);
        if (current && equal(get(config, saved), saved.after) && !equal(current.after, saved.after)) current.before = copy(saved.after);
      }
      this.persist();
    }
    resolve(useLocal) {
      if (useLocal) for (const edit of this.pending) { edit.before = copy(get(this.remote, edit)); edit.conflict = false; }
      else this.pending = [];
      this.persist();
      return this.receive(this.remote, this.version);
    }
  }
  if (typeof module !== 'undefined' && module.exports) module.exports = ConditionalAdminSync;
  else root.ConditionalAdminSync = ConditionalAdminSync;
})(typeof window !== 'undefined' ? window : globalThis);
