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

/**
 * Game Library Manager v5.0 - Enhanced UX Edition
 * A full-featured Docker game library manager with premium UX
 *
 * Features:
 * - Bulk selection and run multiple games
 * - .bat file download for Windows (double-click to run)
 * - Full Docker paths for michadockermisha/backup repo
 * - Custom mount path selection
 * - Enhanced animations and micro-interactions
 * - Improved mobile experience
 * - Scroll-to-top functionality
 * - Keyboard shortcuts
 * - Smooth loading states
 */

class GameLibrary {
    constructor() {
        this.games = [];
        this.tabs = [];
        this.times = {};
        this.imageSizes = {};
        this.datesAdded = {};
        this.filteredGames = [];
        this.selectedGames = new Set();
        this.installedGames = new Set();
        this.manualInstalledGames = new Set();
        this.scannedInstalledGames = null;
        this.installedScanEntries = [];
        this.installedScanMatches = new Map();
        this.installedScannerState = 'starting';
        this.installedScanIntervalMs = 5000;
        this.installedScanInterval = null;
        this.installedScanInFlight = false;
        this.installedScanSignature = '';
        this._adminSaveInFlight = null;
        this._adminSaveDirty = false;
        this.hiddenTabs = new Set();
        this.wishlist = new Set(JSON.parse(localStorage.getItem('gameWishlist') || '[]'));
        this.currentTab = 'all';
        this.searchQuery = '';
        const _savedSort = (() => { try { return JSON.parse(localStorage.getItem('gameLibrarySortPref') || 'null'); } catch(e) { return null; } })();
        this.sortBy = (_savedSort && _savedSort.by) || 'date';
        this.sortOrder = (_savedSort && _savedSort.order) || 'desc';
        this.ratings = (() => { try { return JSON.parse(localStorage.getItem('gameLibraryRatings') || '{}'); } catch(e) { return {}; } })();
        this.showInstalledOnly = false;
        this.isAdmin = false;
        this.gameTags = this.loadGameTags();   // Map: gameId -> Set<string>
        this.activeTagFilter = null;            // Currently active tag filter
        this.minRatingFilter = 0;              // Minimum star rating filter (0 = show all)
        // SHA-256 hash of admin password - NEVER store plaintext passwords in source code
        // Password: Blackablacka3!
        this.adminHash = 'fba92b2c989a5072544ca49d7f75db2005e6479bf286a38902de90e487230762';

        // ADMIN-ONLY TABS: These tabs and ALL games within them are ONLY visible to admins
        // Regular users will NEVER see these tabs or their contents under any circumstances
        this.ADMIN_ONLY_TABS = new Set([
            'not_for_me',      // meh
            'finished',        // Finished
            'mybackup',        // MyBackup
            'oporationsystems', // OporationSystems
            'music',           // music
            'win11maintaince', // Win11Maintaince
            '3th_party_tools', // 3th party tools
            'gamedownloaders'  // GameDownloaders
        ]);

        this.settings = this.loadSettings();
        this.dockerSyncIntervalMs = 15000;
        this.lastDockerTagCount = null;
        this.lastDockerLatestTag = null;

        this.init();
    }

    async init() {
        this.showLoading(true);
        this.detectOS();
        this.bindEvents();
        this.applySettings();
        this.setupScrollEffects();
        this.setupMobileSidebar();
        this.setupKeyboardHints();

        // CRITICAL: Ensure non-admin state on page load - users must login to get admin access
        this.ensureNonAdminState();

        try {
            await this.loadData();

            // CRITICAL: Load admin config from server AFTER games are loaded
            // so game category overrides can be applied to actual game objects
            await this.loadAdminConfigFromServer();
            this.applyUrlState();

            this.renderTabs();
            this.renderTagFilterBar();
            this.filterAndRender();
            this.showLoading(false);
            this.updateSelectedCount();
            this.updateStatsDashboard();

            // Start automatic Docker Hub sync for already-open tabs.
            this.startAutoSync();
            
            // Start polling for admin config changes (every 5 seconds)
            this.startAdminConfigPolling();

            // A browser cannot inspect local drives directly. The local companion
            // endpoint, when available, keeps the Installed tab current.
            this.startInstalledScanner();
        } catch (error) {
            console.error('Failed to load data:', error);
            this.showToast('Failed to load game data', 'error');
            this.showLoading(false);
        }
    }

    applyUrlState() {
        const params = new URLSearchParams(window.location.search);
        const tab = params.get('tab');
        if (tab === 'all' || tab === 'wishlist' || tab === 'installed' || this.tabs.some(t => t.id === tab)) {
            this.currentTab = tab;
        }

        if (!params.has('sort') && this.currentTab === 'all') {
            this.sortBy = 'date';
            this.sortOrder = 'desc';
        }
    }
    
    // Poll server for admin configuration changes
    startAdminConfigPolling() {
        this._lastConfigVersion = this._lastConfigVersion || null;
        this._lastAdminConfigSignature = this._lastAdminConfigSignature || null;
        this._vercelMode = false;
        this._pollFailCount = 0;
        // Check for updates every 2 seconds without re-rendering unchanged config.
        this.configPollInterval = setInterval(async () => {
            // Skip polling if we're on Vercel (no API)
            if (this._vercelMode) return;
            
            try {
                // CRITICAL: Cache-busting to ALWAYS get latest changes
                const cacheBuster = `?t=${Date.now()}&v=${Math.random()}`;
                const response = await fetch(`/api/admin-config${cacheBuster}`, {
                    method: 'GET',
                    headers: { 
                        'Content-Type': 'application/json',
                        'Cache-Control': 'no-cache, no-store, must-revalidate',
                        'Pragma': 'no-cache'
                    }
                });
                if (response.ok) {
                    this._pollFailCount = 0; // Reset fail count on success
                    const data = await response.json();
                    if (data.success && data.config) {
                        const signature = this.getAdminConfigSignature(data.config);
                        if (this._lastAdminConfigSignature !== null && signature !== this._lastAdminConfigSignature) {
                            // Config changed - reload everything from server
                            await this.loadAdminConfigFromServer();
                            this.renderTabs();
                            this.filterAndRender();
                            console.log('Config updated from server:', data.configVersion || signature);
                        }
                        this._lastAdminConfigSignature = signature;
                        this._lastConfigVersion = data.configVersion || signature;
                    }
                } else if (response.status === 404) {
                    this._pollFailCount++;
                    if (this._pollFailCount >= 3) {
                        this._vercelMode = true;
                        console.log('API not available after 3 attempts, stopping polling (Vercel mode)');
                        clearInterval(this.configPollInterval);
                    }
                }
            } catch (e) {
                // silent
            }
        }, 2000); // Every 2 seconds for instant updates

        console.log('⚡ Admin config polling started: checking every 2 seconds for instant updates');
    }

    getAdminConfigSignature(config) {
        const categories = config?.gameCategories && typeof config.gameCategories === 'object'
            ? config.gameCategories
            : {};
        const stableCategories = Object.keys(categories).sort().reduce((result, key) => {
            result[key] = categories[key];
            return result;
        }, {});
        return JSON.stringify({
            hiddenTabs: Array.isArray(config?.hiddenTabs) ? [...new Set(config.hiddenTabs)].sort() : [],
            gameCategories: stableCategories,
            tabs: Array.isArray(config?.tabs) ? config.tabs : null
        });
    }

    startAutoSync() {
        // Poll a lightweight first-page summary, then run the complete sync only
        // when Docker Hub exposes a changed count or latest tag.
        this.syncInterval = setInterval(() => {
            this.autoSyncDockerHub();
        }, this.dockerSyncIntervalMs);

        // Also update sync button to show auto-sync is active
        const syncBtn = document.getElementById('syncDockerBtn');
        if (syncBtn) {
            syncBtn.title = 'Auto-syncing every 15s (click to sync now)';
        }

        console.log('🔄 Auto-sync started: checking Docker Hub every 15 seconds');
    }

    async autoSyncDockerHub() {
        try {
            const dockerUser = this.settings.dockerUsername || 'michadockermisha';
            const repoName = this.settings.repoName || 'backup';
            const changed = await this.hasDockerHubChanged(dockerUser, repoName);
            if (!changed) return;

            const result = await this.syncDockerHubTags({ silent: true });
            if (result.added > 0) {
                this.showToast(`🆕 ${result.added} new Docker tag(s) added!`, 'success');
            }
        } catch (error) {
            console.error('Auto-sync error:', error);
        }
    }

    async loadData() {
        // Cache-bust to always get fresh data
        const cacheBuster = `?_=${Date.now()}`;

        const [gamesData, tabsData, timesData, imageSizesData, datesAddedData] = await Promise.all([
            fetch('data/games.json' + cacheBuster).then(r => r.json()),
            fetch('data/tabs.json' + cacheBuster).then(r => r.json()),
            fetch('data/times.json' + cacheBuster).then(r => r.json()),
            fetch('data/image-sizes.json' + cacheBuster).then(r => r.json()),
            fetch('data/dates-added.json' + cacheBuster).then(r => r.json())
        ]);

        this.games = gamesData;
        this.tabs = tabsData;
        this.times = timesData;
        this.imageSizes = imageSizesData;
        this.datesAdded = datesAddedData;
        this.normalizeAllGameMetadata();

        console.log(`📦 Loaded ${this.games.length} games from games.json`);

        // Clear old localStorage if game count changed significantly (new version deployed)
        const savedCount = localStorage.getItem('lastGameCount');
        if (savedCount && Math.abs(parseInt(savedCount) - this.games.length) > 10) {
            console.log(`🔄 Game count changed (${savedCount} → ${this.games.length}), clearing cache`);
            localStorage.removeItem('gameLibraryGames');
            localStorage.removeItem('newGamesFromDocker');
        }
        localStorage.setItem('lastGameCount', this.games.length.toString());

        // Load any saved game category changes from localStorage (only categories, not game list)
        this.loadSavedGameChanges();

        // Load installed games from localStorage
        this.loadInstalledGames();

        // Update counts immediately
        document.getElementById('gameCount').textContent = this.games.length;
        document.getElementById('tabCount').textContent = `${this.tabs.length} tabs`;

        // Merge every current Docker Hub tag before rendering so new images become
        // downloadable immediately after they are pushed to michadockermisha/backup.
        await this.syncDockerHubTags({ silent: true });
    }

    async syncDockerHubTags(options = {}) {
        const silent = !!options.silent;
        try {
            const dockerUser = this.settings.dockerUsername || 'michadockermisha';
            const repoName = this.settings.repoName || 'backup';

            // Fetch all tags from Docker Hub
            const allTags = await this.fetchAllDockerTags(dockerUser, repoName);

            if (allTags.length === 0) {
                console.log('No tags fetched from Docker Hub');
                return { added: 0, updated: 0, fetched: 0 };
            }

            console.log(`Fetched ${allTags.length} tags from Docker Hub`);
            const result = this.mergeDockerHubTags(allTags, dockerUser, repoName);
            this.updateGameCountDisplay();
            if (result.addedIds && result.addedIds.length > 0) {
                await this.enrichDockerOnlyGames(result.addedIds, { silent });
                this.updateGameCountDisplay();
            }

            if (result.added > 0 || result.updated > 0) {
                this.updateGameCountDisplay();
                this.renderTabs();
                this.filterAndRender();
                this.updateStatsDashboard();
            }

            if (result.added > 0 && !silent) {
                this.showToast(`Found ${result.added} new Docker Hub tag(s)!`, 'success');
            }

            this.lastDockerTagCount = allTags.length;
            this.lastDockerLatestTag = allTags[0]?.name || this.lastDockerLatestTag;
            return { ...result, fetched: allTags.length, latestTagName: allTags[0]?.name || null };
        } catch (error) {
            console.error('Failed to sync Docker Hub tags:', error);
            if (!silent) {
                this.showToast('Docker Hub sync failed: ' + error.message, 'error');
            }
            return { added: 0, updated: 0, fetched: 0, error };
        }
    }

    mergeDockerHubTags(allTags, dockerUser, repoName) {
        const gamesById = new Map(this.games.map(game => [game.id, game]));
        let added = 0;
        let updated = 0;
        const addedIds = [];

        for (const tag of allTags) {
            if (!tag || !tag.name) continue;

            const id = tag.name;
            const dockerImage = `${dockerUser}/${repoName}:${id}`;
            const dockerImageUrl = `https://hub.docker.com/r/${dockerUser}/${repoName}/tags?name=${encodeURIComponent(id)}`;
            const date = this.normalizeDockerTimestamp(tag.last_updated);
            const sizeGb = tag.full_size ? Math.round(tag.full_size / 1073741824 * 100) / 100 : null;
            let game = gamesById.get(id);

            if (!game) {
                const name = this.formatGameName(id);
                const category = this.detectBestCategory({ id, name });
                game = {
                    id,
                    name,
                    category,
                    dockerImage,
                    dockerImageUrl,
                    image: this.createGeneratedCoverDataUrl(id),
                    time: this.getGenreEstimate({ id, name, category })
                };
                this.ensureGameDetails(game);
                this.games.push(game);
                gamesById.set(id, game);
                added++;
                addedIds.push(id);
            } else {
                game.dockerImage = this.isRunnableDockerImage(game.dockerImage) ? game.dockerImage : dockerImage;
                game.dockerImageUrl = dockerImageUrl;
                if (!game.category || game.category === 'new') {
                    game.category = this.detectBestCategory(game);
                }
                if (!game.image) game.image = this.createGeneratedCoverDataUrl(id);
                if (game.time == null && this.times[id] == null) {
                    this.times[id] = this.getGenreEstimate(game);
                }
                this.ensureGameDetails(game, true);
            }

            if (sizeGb !== null) this.imageSizes[id] = sizeGb;
            if (date) this.datesAdded[id] = date;
            this.ensureGameDetails(game, true);

            if (gamesById.has(id) && !addedIds.includes(id)) {
                const before = JSON.stringify({
                    dockerImage: game.dockerImage,
                    dockerImageUrl: game.dockerImageUrl,
                    category: game.category,
                    image: game.image,
                    time: game.time,
                    timeLookup: this.times[id],
                    description: game.description,
                    details: game.details,
                    size: this.imageSizes[id],
                    date: this.datesAdded[id]
                });
                const previousTagState = this._lastDockerMergeState?.[id];
                if (previousTagState && previousTagState !== before) updated++;
                if (!this._lastDockerMergeState) this._lastDockerMergeState = {};
                this._lastDockerMergeState[id] = before;
            }
        }

        const tabIds = new Set(this.tabs.map(tab => tab.id));
        for (const id of addedIds) {
            const game = gamesById.get(id);
            if (game?.category && !tabIds.has(game.category)) {
                this.tabs.push({ id: game.category, name: this.formatCategoryLabel(game.category) });
                tabIds.add(game.category);
            }
        }

        if (added > 0 && !tabIds.has('new')) {
            this.tabs.push({ id: 'new', name: 'New', icon: '🆕' });
        }

        if (addedIds.length > 0) {
            this.saveNewGames(addedIds);
        }

        return { added, updated, addedIds };
    }

    updateGameCountDisplay() {
        const gameCount = document.getElementById('gameCount');
        if (gameCount) {
            gameCount.textContent = this.games.length;
        }
    }

    normalizeAllGameMetadata() {
        for (const game of this.games) {
            if (!game || !game.id) continue;
            this.normalizeGameMetadata(game, this.settings.dockerUsername, this.settings.repoName);
        }
    }

    normalizeGameMetadata(game, dockerUser = 'michadockermisha', repoName = 'backup') {
        game.name = game.name || this.formatGameName(game.id);
        game.category = game.category || 'new';
        const imageOverride = this.getKnownImageOverride(game.id);
        if (imageOverride) {
            game.image = imageOverride;
        }
        game.image = game.image || this.createGeneratedCoverDataUrl(game.id);

        const dockerImage = `${dockerUser}/${repoName}:${game.id}`;
        if (!this.isRunnableDockerImage(game.dockerImage)) {
            game.dockerImage = dockerImage;
        }
        game.dockerImageUrl = this.getDockerHubTagUrl(game.id, dockerUser, repoName);

        if (game.time == null && this.times[game.id] == null) {
            this.times[game.id] = this.getGenreEstimate(game);
        }

        if (!this.datesAdded[game.id]) {
            this.datesAdded[game.id] = new Date().toISOString();
        }

        this.ensureGameDetails(game);
        return game;
    }

    ensureGameDetails(game, refresh = false) {
        if (!refresh && game.description && game.details) return;

        const time = this.getGameTime(game);
        const size = this.imageSizes[game.id];
        const timeText = (time != null && time !== '') ? `~${time} hours` : 'approximate time unavailable';
        const sizeText = (size != null && size !== '') ? `${size} GB Docker image` : 'Docker image size pending from Docker Hub';
        const details = `${game.name} is available from Docker tag ${game.id}. Category: ${game.category || 'uncategorized'}. Time to beat: ${timeText}. Size: ${sizeText}.`;

        game.description = refresh ? details : (game.description || details);
        game.details = refresh ? details : (game.details || details);
    }

    isRunnableDockerImage(value) {
        return typeof value === 'string' && /^[^/\s]+\/[^:\s]+:.+/.test(value) && !value.startsWith('http');
    }

    getDockerHubTagUrl(id, dockerUser = 'michadockermisha', repoName = 'backup') {
        return `https://hub.docker.com/r/${dockerUser}/${repoName}/tags?name=${encodeURIComponent(id)}`;
    }

    getDisplayImageUrl(value) {
        if (!value || typeof value !== 'string') return value;
        if (!/^https?:\/\//i.test(value)) return value;
        return `/api/image-proxy?url=${encodeURIComponent(value)}`;
    }

    normalizeLookupKey(value) {
        return String(value || '').toLowerCase().replace(/[^a-z0-9]/g, '');
    }

    getKnownImageOverride(id) {
        const key = this.normalizeLookupKey(id);
        const overrides = {
            '007firstlight': 'https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/3768760/93f5c0de8db2d42413f8c1eb4bdb1ccb80f7de65/capsule_616x353.jpg'
        };
        return overrides[key] || null;
    }

    normalizeDockerTimestamp(value) {
        if (typeof value === 'string') {
            const timestamp = Date.parse(value);
            if (!Number.isNaN(timestamp)) return new Date(timestamp).toISOString();
        }
        return null;
    }

    getAddedTimestamp(game) {
        const rawDate = game && game.id ? this.datesAdded[game.id] : null;
        if (!rawDate) return null;

        const timestamp = Date.parse(rawDate);
        return Number.isNaN(timestamp) ? null : timestamp;
    }

    getGameTime(game) {
        return (game && game.time != null) ? game.time : this.times[game.id];
    }

    detectBestCategory(game) {
        const combined = ((game.name || '') + ' ' + (game.id || '') + ' ' + (game.genre || '')).toLowerCase();
        const compact = combined.replace(/[^a-z0-9]/g, '');
        const rules = [
            ['oporationsystems', ['windows', 'win11', 'ubuntu', 'linux', 'debian', 'fedora', 'operation', 'system']],
            ['3th_party_tools', ['tool', 'utility', 'driver', 'launcher', 'installer', 'browser', 'office', 'editor', 'server']],
            ['gamedownloaders', ['downloader', 'download', 'torrent']],
            ['soulslike', ['souls', 'elden', 'sekiro', 'nioh', 'liesofp', 'khazan', 'lordsofthefallen']],
            ['localcoop', ['coop', 'co-op', 'party', 'overcooked', 'movingout', 'ittakestwo', 'unravel', 'lego']],
            ['nintendo/switch', ['nintendo', 'switch', 'zelda', 'mario', 'kirby', 'pokemon', 'metroid', 'bayonetta', 'xenoblade']],
            ['shooters', ['shooter', 'fps', 'tps', 'doom', 'quake', 'wolfenstein', 'borderlands', 'callofduty', 'battlefield', 'sniper', 'halo']],
            ['openworld', ['openworld', 'open world', 'farcry', 'assassin', 'gta', 'cyberpunk', 'watchdogs', 'reddead', 'justcause', 'avatar', 'mafia']],
            ['hacknslash', ['hacknslash', 'hack and slash', 'warriors', 'devilmaycry', 'ninjagaiden', 'bayonetta', 'darksiders', 'hades']],
            ['storydriven', ['story', 'lifeisstrange', 'telltale', 'walkingdead', 'detroit', 'quantic', 'plague', 'edith', 'firewatch']],
            ['platformers', ['platform', 'metroidvania', 'ori', 'hollowknight', 'celeste', 'rayman', 'sonic', 'crash', 'spyro']],
            ['rpg', ['rpg', 'jrpg', 'persona', 'finalfantasy', 'dragonquest', 'starfield', 'baldurs', 'witcher', 'fallout', 'skyrim', 'yakuza']],
            ['adventure', ['adventure', 'quest', 'journey', 'tomb', 'uncharted', 'indiana', 'sherlock', 'oceanhorn', 'firstlight']],
            ['racing', ['racing', 'race', 'forza', 'needforspeed', 'dirt', 'wrc', 'motogp', 'rally']],
            ['puzzle', ['puzzle', 'portal', 'witness', 'talos', 'myst', 'escape room']],
            ['strategy', ['strategy', 'tactics', 'tactical', 'rts', 'civilization', 'xcom', 'anno', 'totalwar', 'warhammer', 'frostpunk']],
            ['sports', ['sport', 'sports', 'fifa', 'fc24', 'nba', 'nfl', 'football', 'soccer', 'tennis', 'golf', 'wwe']],
            ['fighting', ['fighting', 'fighter', 'tekken', 'streetfighter', 'mortalkombat', 'guiltygear', 'brawler']],
            ['simulators', ['simulator', 'simulation', 'tycoon', 'manager', 'farming', 'truck', 'flight', 'train', 'cities', 'planet']],
            ['music', ['music', 'rhythm', 'guitar', 'dance', 'beat']],
            ['chill', ['chill', 'cozy', 'cosy', 'stardew', 'farm', 'garden', 'unpacking', 'wanderstop']],
            ['action', ['action', 'combat', 'war', 'dead', 'dark', 'shadow', 'ghost', 'hunter', 'revenge', 'steel', 'berserker']]
        ];

        for (const [category, terms] of rules) {
            if (terms.some(term => combined.includes(term) || compact.includes(term.replace(/[^a-z0-9]/g, '')))) {
                return category;
            }
        }

        return 'new';
    }

    async fetchAllDockerTags(dockerUser, repoName) {
        const syncBtn = document.getElementById('syncDockerBtn');
        if (syncBtn) {
            syncBtn.classList.add('syncing');
            syncBtn.textContent = '🔄 Syncing...';
        }

        try {
            const url = `/api/docker-tags?user=${encodeURIComponent(dockerUser)}&repo=${encodeURIComponent(repoName)}&_=${Date.now()}`;
            const response = await fetch(url, {
                headers: { Accept: 'application/json' },
                cache: 'no-store'
            });
            if (!response.ok) {
                throw new Error(`Docker tag API HTTP ${response.status}`);
            }
            const data = await response.json();
            if (!Array.isArray(data.tags) || data.tags.length === 0) {
                throw new Error(data.error || 'Docker tag API returned incomplete data');
            }

            if (!data.success) {
                console.warn(`Docker Hub API returned usable tags with a transient completeness warning: ${data.fetched}/${data.count}`);
            }
            console.log(`✅ Docker Hub API returned ${data.fetched}/${data.count} tags`);
            this.lastDockerTagCount = data.fetched || data.count || data.tags.length;
            this.lastDockerLatestTag = data.tags[0]?.name || null;
            return data.tags;
        } catch (error) {
            console.error('Error fetching Docker tags:', error);
            return [];
        } finally {
            // Reset sync button
            if (syncBtn) {
                syncBtn.classList.remove('syncing');
                syncBtn.textContent = '🔄 Sync';
            }
        }
    }

    async hasDockerHubChanged(dockerUser, repoName) {
        try {
            const summary = await this.fetchDockerTagSummary(dockerUser, repoName);
            if (!summary) return true;

            const latestName = summary.latestTag?.name || null;
            const latestMissing = latestName && !this.games.some(game => game.id === latestName);
            const countChanged = this.lastDockerTagCount !== null && summary.count !== this.lastDockerTagCount;

            if (this.lastDockerTagCount === null) this.lastDockerTagCount = summary.count;
            if (this.lastDockerLatestTag === null) this.lastDockerLatestTag = latestName;

            return !!(countChanged || latestMissing || (latestName && latestName !== this.lastDockerLatestTag));
        } catch (error) {
            console.error('Docker Hub summary check failed:', error);
            return false;
        }
    }

    async fetchDockerTagSummary(dockerUser, repoName) {
        const url = `/api/docker-tags?user=${encodeURIComponent(dockerUser)}&repo=${encodeURIComponent(repoName)}&summary=1&page_size=1&_=${Date.now()}`;
        const response = await fetch(url, {
            headers: {
                Accept: 'application/json',
                'Cache-Control': 'no-cache, no-store, must-revalidate',
                Pragma: 'no-cache'
            },
            cache: 'no-store'
        });

        if (!response.ok) {
            throw new Error(`Docker tag summary API HTTP ${response.status}`);
        }

        const data = await response.json();
        if (!data.success || typeof data.count !== 'number') {
            throw new Error(data.error || 'Docker tag summary API returned incomplete data');
        }

        return data;
    }

    formatGameName(tagName) {
        // Convert tag name to readable game name
        let name = tagName;

        // Docker tags often use hyphen/underscore separators.
        name = name.replace(/[-_]+/g, ' ');

        // Add spaces before numbers
        name = name.replace(/(\d+)/g, ' $1');

        // Add spaces before capital letters
        name = name.replace(/([a-z])([A-Z])/g, '$1 $2');

        // Capitalize first letter of each word
        name = name.split(' ').map(word =>
            word.charAt(0).toUpperCase() + word.slice(1)
        ).join(' ');

        // Clean up multiple spaces
        name = name.replace(/\s+/g, ' ').trim();

        return name;
    }

    async enrichDockerOnlyGames(gameIds, options = {}) {
        const silent = !!options.silent;
        const targets = gameIds
            .map(id => this.games.find(game => game.id === id))
            .filter(Boolean);

        if (targets.length === 0) return { enriched: 0, failed: 0 };

        let enriched = 0;
        let failed = 0;
        const batchSize = 3;

        for (let index = 0; index < targets.length; index += batchSize) {
            const batch = targets.slice(index, index + batchSize);
            const results = await Promise.allSettled(batch.map(game => this.fetchGameMetadata(game)));

            results.forEach((result, resultIndex) => {
                const game = batch[resultIndex];
                if (result.status !== 'fulfilled' || !result.value) {
                    failed++;
                    return;
                }

                if (this.applyGameMetadata(game, result.value)) {
                    enriched++;
                }
            });
        }

        if (enriched > 0) {
            this.filterAndRender();
            this.updateStatsDashboard();
        }

        if (!silent && enriched > 0) {
            this.showToast(`Enriched ${enriched} new Docker game(s) with real metadata`, 'success');
        }

        if (failed > 0) {
            console.warn(`Metadata enrichment failed for ${failed} Docker game(s)`);
        }

        return { enriched, failed };
    }

    async fetchGameMetadata(game) {
        const url = `/api/game-metadata?id=${encodeURIComponent(game.id)}&name=${encodeURIComponent(game.name || game.id)}&category=${encodeURIComponent(game.category || '')}&_=${Date.now()}`;
        const response = await fetch(url, {
            headers: { Accept: 'application/json' },
            cache: 'no-store'
        });

        if (!response.ok) {
            throw new Error(`Game metadata API HTTP ${response.status}`);
        }

        const data = await response.json();
        if (!data.success) {
            throw new Error(data.error || 'Game metadata API returned incomplete data');
        }

        return data;
    }

    applyGameMetadata(game, metadata) {
        let changed = false;

        if (metadata.name && metadata.name !== game.name) {
            game.name = metadata.name;
            changed = true;
        }

        if (metadata.category && game.category !== metadata.category) {
            game.category = metadata.category;
            changed = true;
        }

        if (metadata.image && (!game.image || this.isGeneratedCover(game.image))) {
            game.image = metadata.image;
            changed = true;
        }

        if (metadata.time != null && metadata.time !== '') {
            const nextTime = Number(metadata.time);
            if (!Number.isNaN(nextTime) && this.times[game.id] !== nextTime && game.time !== nextTime) {
                game.time = nextTime;
                this.times[game.id] = nextTime;
                changed = true;
            }
        }

        if (metadata.steamAppId && metadata.image) {
            const cache = this.getSteamCoverCache();
            const cacheKey = game.name.toLowerCase().replace(/[^a-z0-9]/g, '');
            cache[cacheKey] = metadata.image;
            this.saveSteamCoverCache();
        }

        if (changed) {
            this.ensureGameDetails(game, true);
        }

        return changed;
    }

    isGeneratedCover(image) {
        return typeof image === 'string' && image.startsWith('data:image/svg+xml');
    }

    createGeneratedCoverDataUrl(gameId) {
        const title = this.formatGameName(gameId);
        const safeTitle = title
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;');
        const hue = Array.from(gameId).reduce((acc, char) => acc + char.charCodeAt(0), 0) % 360;
        const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 300 400"><defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop stop-color="hsl(${hue},70%,28%)"/><stop offset="1" stop-color="hsl(${(hue + 70) % 360},80%,16%)"/></linearGradient></defs><rect width="300" height="400" fill="url(#g)"/><circle cx="245" cy="65" r="70" fill="rgba(255,255,255,.09)"/><text x="150" y="145" text-anchor="middle" font-size="54">🎮</text><text x="150" y="235" text-anchor="middle" fill="#fff" font-family="Verdana,Arial,sans-serif" font-size="24" font-weight="700">${safeTitle}</text><text x="150" y="350" text-anchor="middle" fill="rgba(255,255,255,.72)" font-family="Verdana,Arial,sans-serif" font-size="14">Docker Hub Ready</text></svg>`;
        return `data:image/svg+xml;charset=UTF-8,${encodeURIComponent(svg)}`;
    }

    saveNewGames(newGameIds) {
        try {
            const saved = localStorage.getItem('newGamesFromDocker') || '[]';
            const existing = JSON.parse(saved);
            const combined = [...new Set([...existing, ...newGameIds])];
            localStorage.setItem('newGamesFromDocker', JSON.stringify(combined));
        } catch (e) {
            console.error('Failed to save new games:', e);
        }
    }

    loadSavedNewGames() {
        try {
            const saved = localStorage.getItem('newGamesFromDocker');
            return saved ? JSON.parse(saved) : [];
        } catch {
            return [];
        }
    }

    async manualSync() {
        const btn = document.getElementById('syncDockerBtn');
        const originalText = btn.textContent;

        btn.textContent = '⏳ Syncing...';
        btn.disabled = true;

        try {
            await this.syncDockerHubTags();
            document.getElementById('gameCount').textContent = this.games.length;
            this.renderTabs();
            this.filterAndRender();
        } catch (error) {
            this.showToast('Sync failed: ' + error.message, 'error');
        } finally {
            btn.textContent = originalText;
            btn.disabled = false;
        }
    }

    // =============================================
    // ADD IMAGES - Scan all games, fetch missing/wrong cover images
    // =============================================
    async scanAndAddImages() {
        const btn = document.getElementById('addImagesBtn');
        btn.disabled = true;
        btn.textContent = '⏳ Scanning...';

        try {
            // Clear stale cache entries
            const cache = this.getSteamCoverCache();
            const knownIds = this.getKnownSteamAppIds();
            let cleared = 0;
            for (const key of Object.keys(cache)) {
                if (key === '_version') continue;
                if (cache[key] === 'none' || (knownIds[key] && !cache[key].includes(knownIds[key]))) {
                    delete cache[key];
                    cleared++;
                }
            }
            cache._version = 'v4-forced';
            this.saveSteamCoverCache();
            if (cleared > 0) {
                this.showToast(`Cleared ${cleared} stale cache entries`, 'info');
            }

            // Find all games without local images (will need online fetch)
            const gamesNeedingImages = [];
            for (const game of this.games) {
                const cacheKey = game.id.toLowerCase().replace(/[^a-z0-9]/g, '');
                const cached = cache[cacheKey];
                // Skip if already has a valid cached URL (not 'none')
                if (cached && cached !== 'none' && cached.startsWith('http')) continue;
                gamesNeedingImages.push(game);
            }

            this.showToast(`Found ${gamesNeedingImages.length} games needing images. Fetching...`, 'info');
            btn.textContent = `⏳ 0/${gamesNeedingImages.length}`;

            let found = 0;
            let failed = 0;
            const batchSize = 3; // Process 3 at a time to avoid rate limits

            for (let i = 0; i < gamesNeedingImages.length; i += batchSize) {
                const batch = gamesNeedingImages.slice(i, i + batchSize);
                const results = await Promise.allSettled(
                    batch.map(game => this.fetchSteamCoverUrl(game.name))
                );

                for (let j = 0; j < results.length; j++) {
                    if (results[j].status === 'fulfilled' && results[j].value) {
                        found++;
                    } else {
                        failed++;
                    }
                }

                btn.textContent = `⏳ ${Math.min(i + batchSize, gamesNeedingImages.length)}/${gamesNeedingImages.length}`;

                // Small delay between batches
                if (i + batchSize < gamesNeedingImages.length) {
                    await new Promise(r => setTimeout(r, 300));
                }
            }

            // Refresh the grid to show new images
            this.filterAndRender();
            this.showToast(`Images done! Found: ${found}, Not found: ${failed}`, found > 0 ? 'success' : 'warning');
        } catch (error) {
            this.showToast('Image scan failed: ' + error.message, 'error');
        } finally {
            btn.textContent = '🖼️ Add Images';
            btn.disabled = false;
        }
    }

    // =============================================
    // ADD TIMES - Fetch HLTB data for all games
    // =============================================
    async scanAndAddTimes() {
        const btn = document.getElementById('addTimesBtn');
        btn.disabled = true;
        btn.textContent = '⏳ Scanning...';

        try {
            // Find games without time data
            const gamesNeedingTimes = [];
            for (const game of this.games) {
                if (!this.times[game.id] && this.times[game.id] !== 0) {
                    gamesNeedingTimes.push(game);
                }
            }

            this.showToast(`Found ${gamesNeedingTimes.length} games without time data. Fetching...`, 'info');
            btn.textContent = `⏳ 0/${gamesNeedingTimes.length}`;

            let found = 0;
            let failed = 0;
            const batchSize = 3;

            for (let i = 0; i < gamesNeedingTimes.length; i += batchSize) {
                const batch = gamesNeedingTimes.slice(i, i + batchSize);
                const results = await Promise.allSettled(
                    batch.map(game => this.fetchHLTBTime(game))
                );

                for (let j = 0; j < results.length; j++) {
                    if (results[j].status === 'fulfilled' && results[j].value !== null) {
                        const game = batch[j];
                        const val = results[j].value;
                        if (val && typeof val === 'object' && val.estimated) {
                            // Fallback estimate — store hours and mark with a note for the UI
                            this.times[game.id] = val.hours;
                            if (!this._estimatedGames) this._estimatedGames = [];
                            this._estimatedGames.push(game.name || game.id);
                            found++;
                        } else {
                            this.times[game.id] = val;
                            found++;
                        }
                    } else {
                        failed++;
                    }
                }

                btn.textContent = `⏳ ${Math.min(i + batchSize, gamesNeedingTimes.length)}/${gamesNeedingTimes.length}`;

                if (i + batchSize < gamesNeedingTimes.length) {
                    await new Promise(r => setTimeout(r, 500));
                }
            }

            // Save updated times to server
            if (found > 0) {
                try {
                    await fetch('/api/save-times', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify(this.times)
                    });
                } catch (e) {
                    console.error('Failed to save times to server:', e);
                }
            }

            // Refresh grid
            this.filterAndRender();
            const estimatedCount = (this._estimatedGames || []).length;
            this._estimatedGames = [];
            let toastMsg = `Times done! Found: ${found}, Not found: ${failed}`;
            if (estimatedCount > 0) {
                toastMsg += ` (${estimatedCount} estimated ~10h — not found on HLTB)`;
            }
            this.showToast(toastMsg, found > 0 ? 'success' : 'warning');
        } catch (error) {
            this.showToast('Time scan failed: ' + error.message, 'error');
        } finally {
            btn.textContent = '⏱️ Add Times';
            btn.disabled = false;
        }
    }

    // Fetch HLTB time for a single game using multiple sources
    async fetchHLTBTime(game) {
        const rawName = game.name || game.id;
        const normalizedName = this.normalizeGameName(rawName);
        // Use normalized name for lookup; fall back to original split if different
        const gameName = normalizedName;

        // Check known times database first
        const knownTime = this.getKnownGameTimes()[game.id.toLowerCase().replace(/[^a-z0-9]/g, '')];
        if (knownTime !== undefined) return knownTime;

        const corsProxies = [
            'https://corsproxy.io/?',
            'https://api.allorigins.win/raw?url=',
            'https://api.codetabs.com/v1/proxy?quest='
        ];

        // Source 1: HLTB search via CORS proxy
        // Try normalized name first, then fall back to original raw name
        const originalName = this.splitGameName(rawName);
        const searchNames = normalizedName !== originalName
            ? [normalizedName, originalName]
            : [normalizedName];
        for (const proxy of corsProxies) {
            for (const searchName of searchNames) {
                try {
                    const searchUrl = `https://howlongtobeat.com/api/search`;
                    const response = await fetch(proxy + encodeURIComponent(searchUrl), {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({
                            searchType: 'games',
                            searchTerms: searchName.split(' '),
                            searchPage: 1,
                            size: 1,
                            searchOptions: {
                                games: { userId: 0, platform: '', sortCategory: 'popular', rangeCategory: 'main', rangeTime: { min: null, max: null }, gameplay: { perspective: '', flow: '', genre: '', subGenre: '' }, rangeYear: { min: '', max: '' }, modifier: '' },
                                users: { sortCategory: 'postcount' },
                                lists: { sortCategory: 'follows' },
                                filter: '', sort: 0, randomizer: 0
                            }
                        }),
                        signal: AbortSignal.timeout(10000)
                    });
                    const data = await response.json();
                    if (data && data.data && data.data.length > 0) {
                        const result = data.data[0];
                        // comp_main is main story in seconds
                        const mainHours = result.comp_main ? Math.round(result.comp_main / 3600) : null;
                        if (mainHours && mainHours > 0) return mainHours;
                        // Fallback to comp_plus (main + extras)
                        const plusHours = result.comp_plus ? Math.round(result.comp_plus / 3600) : null;
                        if (plusHours && plusHours > 0) return plusHours;
                    }
                } catch (e) { continue; }
            }
        }

        // Source 2: RAWG API (has playtime data)
        for (const proxy of corsProxies) {
            try {
                const rawgUrl = `https://api.rawg.io/api/games?key=c542e67aec3a4340908f9de9e86038af&search=${encodeURIComponent(gameName)}&page_size=1`;
                const response = await fetch(proxy + encodeURIComponent(rawgUrl), {
                    signal: AbortSignal.timeout(8000)
                });
                const data = await response.json();
                if (data && data.results && data.results.length > 0) {
                    const playtime = data.results[0].playtime;
                    if (playtime && playtime > 0) return playtime;
                }
            } catch (e) { continue; }
        }

        // Source 3: IGDB via Twitch API proxy
        for (const proxy of corsProxies) {
            try {
                const igdbSearch = `https://api.igdb.com/v4/games`;
                // Use a search approach through proxy
                const searchUrl = `https://www.igdb.com/search_autocomplete_all?q=${encodeURIComponent(gameName)}`;
                const response = await fetch(proxy + encodeURIComponent(searchUrl), {
                    signal: AbortSignal.timeout(8000)
                });
                const data = await response.json();
                if (data && data.game_suggest && data.game_suggest.length > 0) {
                    // IGDB doesn't directly return playtime in search, but we tried
                    break;
                }
            } catch (e) { continue; }
        }

        // ── FALLBACK STRATEGY 1: Retry HLTB with alternate name normalizations ──
        const altNames = [];
        // Remove subtitle (everything after : or dash)
        const noSubtitle = gameName.replace(/\s*[:\u2013\u2014\-]\s*.+$/, '').trim();
        if (noSubtitle && noSubtitle !== gameName) altNames.push(noSubtitle);
        // Remove edition/remaster suffixes
        const noEdition = gameName.replace(/\b(remastered|remake|edition|definitive|ultimate|deluxe|complete|collection|hd|goty|anniversary)\b.*/gi, '').trim();
        if (noEdition && noEdition !== gameName && noEdition !== noSubtitle) altNames.push(noEdition);
        // camelCase split from raw id
        const camelSplit = (game.id || '').replace(/([a-z])([A-Z])/g, '$1 $2').replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2').trim();
        if (camelSplit && camelSplit !== gameName) altNames.push(camelSplit);

        for (const altName of altNames) {
            for (const proxy of corsProxies) {
                try {
                    const searchUrl = `https://howlongtobeat.com/api/search`;
                    const response = await fetch(proxy + encodeURIComponent(searchUrl), {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({
                            searchType: 'games',
                            searchTerms: altName.split(' '),
                            searchPage: 1,
                            size: 1,
                            searchOptions: {
                                games: { userId: 0, platform: '', sortCategory: 'popular', rangeCategory: 'main', rangeTime: { min: null, max: null }, gameplay: { perspective: '', flow: '', genre: '', subGenre: '' }, rangeYear: { min: '', max: '' }, modifier: '' },
                                users: { sortCategory: 'postcount' },
                                lists: { sortCategory: 'follows' },
                                filter: '', sort: 0, randomizer: 0
                            }
                        }),
                        signal: AbortSignal.timeout(10000)
                    });
                    const data = await response.json();
                    if (data && data.data && data.data.length > 0) {
                        const result = data.data[0];
                        const mainHours = result.comp_main ? Math.round(result.comp_main / 3600) : null;
                        if (mainHours && mainHours > 0) return mainHours;
                        const plusHours = result.comp_plus ? Math.round(result.comp_plus / 3600) : null;
                        if (plusHours && plusHours > 0) return plusHours;
                    }
                } catch (e) { continue; }
            }
        }

        // ── FALLBACK STRATEGY 2: Retry HLTB with first title word(s) only ──
        const titleWords = gameName.split(' ').filter(w => w.length > 2);
        if (titleWords.length > 1) {
            const shortName = titleWords.slice(0, 2).join(' ');
            for (const proxy of corsProxies) {
                try {
                    const searchUrl = `https://howlongtobeat.com/api/search`;
                    const response = await fetch(proxy + encodeURIComponent(searchUrl), {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({
                            searchType: 'games',
                            searchTerms: shortName.split(' '),
                            searchPage: 1,
                            size: 3,
                            searchOptions: {
                                games: { userId: 0, platform: '', sortCategory: 'popular', rangeCategory: 'main', rangeTime: { min: null, max: null }, gameplay: { perspective: '', flow: '', genre: '', subGenre: '' }, rangeYear: { min: '', max: '' }, modifier: '' },
                                users: { sortCategory: 'postcount' },
                                lists: { sortCategory: 'follows' },
                                filter: '', sort: 0, randomizer: 0
                            }
                        }),
                        signal: AbortSignal.timeout(10000)
                    });
                    const data = await response.json();
                    if (data && data.data && data.data.length > 0) {
                        // Pick the result whose name best matches our game
                        const lowerGame = gameName.toLowerCase();
                        const best = data.data.find(r => r.game_name && lowerGame.includes(r.game_name.toLowerCase().split(' ')[0])) || data.data[0];
                        const mainHours = best.comp_main ? Math.round(best.comp_main / 3600) : null;
                        if (mainHours && mainHours > 0) return mainHours;
                        const plusHours = best.comp_plus ? Math.round(best.comp_plus / 3600) : null;
                        if (plusHours && plusHours > 0) return plusHours;
                    }
                } catch (e) { continue; }
            }
        }

        // ── FALLBACK STRATEGY 3: Genre-based or generic approximate estimate (~10h) ──
        const estimatedHours = this.getGenreEstimate(game);
        // Return an object flagged as estimated so the caller can notify the user
        return { estimated: true, hours: estimatedHours };
    }

    // Return a genre/name-based approximate time estimate for games not found in HLTB
    getGenreEstimate(game) {
        const combined = ((game.name || '') + ' ' + (game.id || '') + ' ' + (game.genre || '') + ' ' + (game.category || '')).toLowerCase();

        if (/\b(utility|server|driver|launcher|installer|browser|office)\b/.test(combined)) return 0;
        if (/\b(visual.novel|kinetic novel|vn)\b/.test(combined)) return 8;
        if (/\b(rpg|role.playing|jrpg|wrpg|open.world)\b/.test(combined)) return 40;
        if (/\b(strategy|rts|turn.based|grand.strategy|4x)\b/.test(combined)) return 30;
        if (/\b(simulation|sandbox|city.builder|farming|tycoon)\b/.test(combined)) return 20;
        if (/\b(action.rpg|arpg|soulslike|souls.like)\b/.test(combined)) return 25;
        if (/\b(metroidvania|metroid)\b/.test(combined)) return 12;
        if (/\b(horror|survival.horror)\b/.test(combined)) return 10;
        if (/\b(shooter|fps|tps)\b/.test(combined)) return 8;
        if (/\b(platformer|platform)\b/.test(combined)) return 8;
        if (/\b(puzzle|point.and.click)\b/.test(combined)) return 10;
        if (/\b(fighting|beat.em.up|brawler)\b/.test(combined)) return 6;
        if (/\b(racing|sport|sports)\b/.test(combined)) return 8;
        if (/\b(roguelike|roguelite|rogue)\b/.test(combined)) return 15;
        if (/\b(indie)\b/.test(combined)) return 10;

        return 10; // Generic default ~10 hours
    }

    // Known game completion times (fallback database)
    getKnownGameTimes() {
        return {
            'doomthedarkages': 16,
            'silenceofthesiren': 25,
            'stillwakesthedeep': 6,
            'scorn': 5,
            'hereticsfork': 12,
            'fortsolis': 4,
            'thekingiswatching': 8,
            'deadlinedelivery': 6,
            'grindsurvivors': 15,
            'formulalegends': 20,
            'kunitsugami': 12,
            'ashrust': 10,
            'cornershopnightshift': 4,
            'caribbeanlegendageofpirates': 30,
            'caribbeanlegenddaggersoffate': 30,
            'dicewithdeath': 8,
            'sculplings': 10,
            'sculptings': 10,
            'dragonkinthebanished': 15,
            'theartisanofgilmith': 12,
            'theratline': 8,
            'royalrevoltsurvivors': 10,
            'tombbraiderililiremastered': 30,
            'tombraiderililiremastered': 30,
            'kaijucrackingcorporation': 10,
            'magicraft': 15,
            'mirrorsedgecatalyst': 12,
            'schedulei': 25,
            'wanderstop': 5,
            'southofmidnight': 12,
            'inzoi': 40,
            'dawnoftheashenqueen': 15,
            'nobodywantstodie': 8,
            'avowed': 25,
            'kingdomcomedeliverance2': 60,
            'eldenringnightreign': 20,
            'splitfiction': 10,
            'atomfall': 15,
            'oblivionremastered': 30,
            'starshiptroopersextermination': 20,
            'starshiptroopers': 12,
            'dragonkinthebanish': 15,
        };
    }

    detectOS() {
        const platform = navigator.platform.toLowerCase();
        const userAgent = navigator.userAgent.toLowerCase();

        if (platform.includes('win') || userAgent.includes('windows')) {
            this.os = 'windows';
        } else if (platform.includes('mac') || userAgent.includes('mac')) {
            this.os = 'mac';
        } else {
            this.os = 'linux';
        }

        document.getElementById('detectedOS').textContent =
            this.os.charAt(0).toUpperCase() + this.os.slice(1);
    }

    bindEvents() {
        // Admin login
        const adminPassword = document.getElementById('adminPassword');
        adminPassword.addEventListener('keypress', (e) => {
            if (e.key === 'Enter') {
                this.attemptAdminLogin(e.target.value);
                e.target.value = '';
            }
        });

        // Admin logout
        document.getElementById('logoutBtn').addEventListener('click', () => {
            this.adminLogout();
        });

        const createCategoryBtn = document.getElementById('createCategoryBtn');
        const newCategoryName = document.getElementById('newCategoryName');
        if (createCategoryBtn && newCategoryName) {
            createCategoryBtn.addEventListener('click', () => {
                this.createCategoryFromInput(newCategoryName.value);
                newCategoryName.value = '';
            });
            newCategoryName.addEventListener('keydown', (e) => {
                if (e.key === 'Enter') {
                    e.preventDefault();
                    this.createCategoryFromInput(newCategoryName.value);
                    newCategoryName.value = '';
                }
            });
        }

        const moveCategoryClose = document.getElementById('moveCategoryClose');
        const moveCategoryModal = document.getElementById('moveCategoryModal');
        const moveCategorySearch = document.getElementById('moveCategorySearch');
        if (moveCategoryClose) {
            moveCategoryClose.addEventListener('click', () => this.closeCategoryMovePanel());
        }
        if (moveCategoryModal) {
            moveCategoryModal.addEventListener('click', (e) => {
                if (e.target === moveCategoryModal) {
                    this.closeCategoryMovePanel();
                }
            });
        }
        if (moveCategorySearch) {
            moveCategorySearch.addEventListener('input', () => this.renderCategoryMoveOptions());
            moveCategorySearch.addEventListener('keydown', (e) => {
                if (e.key === 'Escape') {
                    this.closeCategoryMovePanel();
                }
            });
        }

        // Search
        const searchInput = document.getElementById('searchInput');
        searchInput.addEventListener('input', (e) => {
            this.searchQuery = e.target.value.toLowerCase();
            this.filterAndRender();
        });

        // Keyboard shortcuts
        document.addEventListener('keydown', (e) => {
            if ((e.ctrlKey || e.metaKey) && e.key === 'k') {
                e.preventDefault();
                searchInput.focus();
            }
            if (e.key === 'Escape') {
                this.closeAllModals();
            }
        });

        // Theme toggle
        document.getElementById('themeBtn').addEventListener('click', () => {
            this.toggleTheme();
        });

        // Settings
        document.getElementById('settingsBtn').addEventListener('click', () => {
            this.openSettings();
        });

        document.getElementById('settingsClose').addEventListener('click', () => {
            this.closeModal('settingsModal');
        });

        // Sort button
        document.getElementById('sortBtn').addEventListener('click', (e) => {
            this.toggleSortMenu(e);
        });

        // Sort options
        document.querySelectorAll('.sort-option').forEach(btn => {
            btn.addEventListener('click', (e) => {
                this.handleSort(e.target.dataset.sort, e.target.dataset.order);
            });
        });

        // Modal close
        document.getElementById('modalClose').addEventListener('click', () => {
            this.closeModal('gameModal');
        });

        // Copy command
        document.getElementById('copyCommand').addEventListener('click', () => {
            this.copyToClipboard();
        });

        // Run Docker (single game)
        document.getElementById('runDockerBtn').addEventListener('click', () => {
            this.runInTerminal();
        });

        // Copy Script
        document.getElementById('copyScriptBtn').addEventListener('click', () => {
            this.copyScript();
        });

        // Action bar buttons
        document.getElementById('syncDockerBtn').addEventListener('click', () => {
            this.manualSync();
        });

        document.getElementById('addImagesBtn').addEventListener('click', () => {
            this.scanAndAddImages();
        });

        document.getElementById('addTimesBtn').addEventListener('click', () => {
            this.scanAndAddTimes();
        });

        document.getElementById('showInstalledBtn').addEventListener('click', () => {
            this.toggleInstalledFilter();
        });

        document.getElementById('selectAllBtn').addEventListener('click', () => {
            this.selectAllVisible();
        });

        document.getElementById('deselectAllBtn').addEventListener('click', () => {
            this.deselectAll();
        });

        document.getElementById('runSelectedBtn').addEventListener('click', () => {
            this.runSelectedGames();
        });

        document.getElementById('killContainersBtn').addEventListener('click', () => {
            this.downloadKillScript();
        });

        // Format dropdown for Run Selected button
        document.getElementById('runFormatBtn').addEventListener('click', (e) => {
            e.stopPropagation();
            this.toggleRunFormatMenu(e);
        });

        // Format dropdown for Kill All button
        document.getElementById('killFormatBtn').addEventListener('click', (e) => {
            e.stopPropagation();
            this.toggleKillFormatMenu(e);
        });

        // Format options click handlers for Run menu
        document.querySelectorAll('#runFormatMenu .format-option').forEach(option => {
            option.addEventListener('click', (e) => {
                e.stopPropagation();
                const format = e.target.dataset.format;
                if (this.selectedGames.size > 0) {
                    this.downloadRunScript([...this.selectedGames], format);
                } else {
                    this.showToast('No games selected', 'error');
                }
                document.getElementById('runFormatMenu').style.display = 'none';
            });
        });

        // Format options click handlers for Kill menu
        document.querySelectorAll('#killFormatMenu .format-option').forEach(option => {
            option.addEventListener('click', (e) => {
                e.stopPropagation();
                const format = e.target.dataset.format;
                this.downloadKillScript(format);
                document.getElementById('killFormatMenu').style.display = 'none';
            });
        });

        // Modal .BAT download button
        document.getElementById('runDockerBatBtn').addEventListener('click', () => {
            this.runInTerminal('bat');
        });

        // Move To button
        document.getElementById('moveToBtn').addEventListener('click', (e) => {
            this.toggleMoveToMenu(e);
        });

        // Rating filter dropdown
        document.getElementById('ratingFilter').addEventListener('change', (e) => {
            this.minRatingFilter = parseInt(e.target.value, 10);
            this.filterAndRender();
        });

        // Global mount path
        document.getElementById('globalMountPath').addEventListener('change', (e) => {
            this.settings.mountPath = e.target.value;
            this.saveSettings();
            document.getElementById('mountPath').value = e.target.value;
        });

        // Settings controls
        document.getElementById('gridSize').addEventListener('change', (e) => {
            this.settings.gridSize = e.target.value;
            this.saveSettings();
            this.applySettings();
        });

        document.getElementById('showTimes').addEventListener('change', (e) => {
            this.settings.showTimes = e.target.checked;
            this.saveSettings();
            this.filterAndRender();
        });

        document.getElementById('showCategories').addEventListener('change', (e) => {
            this.settings.showCategories = e.target.checked;
            this.saveSettings();
            this.filterAndRender();
        });

        document.getElementById('dockerUsername').addEventListener('change', (e) => {
            this.settings.dockerUsername = e.target.value;
            this.saveSettings();
        });

        document.getElementById('repoName').addEventListener('change', (e) => {
            this.settings.repoName = e.target.value;
            this.saveSettings();
        });

        document.getElementById('mountPath').addEventListener('change', (e) => {
            this.settings.mountPath = e.target.value;
            this.saveSettings();
            document.getElementById('globalMountPath').value = e.target.value;
        });

        // Export/Import
        document.getElementById('exportData').addEventListener('click', () => {
            this.exportData();
        });

        document.getElementById('importData').addEventListener('click', () => {
            this.importData();
        });

        // Click outside to close menus
        document.addEventListener('click', (e) => {
            const sortMenu = document.getElementById('sortMenu');
            const sortBtn = document.getElementById('sortBtn');
            if (!sortMenu.contains(e.target) && !sortBtn.contains(e.target)) {
                sortMenu.style.display = 'none';
            }

            const moveToMenu = document.getElementById('moveToMenu');
            const moveToBtn = document.getElementById('moveToBtn');
            if (!moveToMenu.contains(e.target) && !moveToBtn.contains(e.target)) {
                moveToMenu.style.display = 'none';
            }

            // Close format menus
            const runFormatMenu = document.getElementById('runFormatMenu');
            const runFormatBtn = document.getElementById('runFormatBtn');
            if (runFormatMenu && !runFormatMenu.contains(e.target) && !runFormatBtn.contains(e.target)) {
                runFormatMenu.style.display = 'none';
            }

            const killFormatMenu = document.getElementById('killFormatMenu');
            const killFormatBtn = document.getElementById('killFormatBtn');
            if (killFormatMenu && !killFormatMenu.contains(e.target) && !killFormatBtn.contains(e.target)) {
                killFormatMenu.style.display = 'none';
            }
        });

        // Modal backdrop click
        document.querySelectorAll('.modal').forEach(modal => {
            modal.addEventListener('click', (e) => {
                if (e.target === modal) {
                    modal.classList.remove('active');
                }
            });
        });
    }

    // Check if a tab is admin-only (hardcoded, cannot be changed by toggling)
    isTabAdminOnly(tabId) {
        return this.ADMIN_ONLY_TABS.has(tabId);
    }

    // Check if a tab should be hidden from non-admin users
    // This includes both admin-only tabs AND tabs marked as hidden by admin
    isTabHiddenForUser(tabId) {
        // Admin-only tabs are ALWAYS hidden for non-admins
        if (this.isTabAdminOnly(tabId) && !this.isAdmin) {
            return true;
        }
        // Regular hidden tabs (toggleable by admin)
        if (this.hiddenTabs.has(tabId) && !this.isAdmin) {
            return true;
        }
        return false;
    }

    escapeHtml(value) {
        return String(value ?? '').replace(/[&<>"']/g, (char) => ({
            '&': '&amp;',
            '<': '&lt;',
            '>': '&gt;',
            '"': '&quot;',
            "'": '&#39;'
        })[char]);
    }

    createCategoryId(name) {
        const base = String(name || '')
            .trim()
            .toLowerCase()
            .replace(/[^a-z0-9]+/g, '_')
            .replace(/^_+|_+$/g, '');
        return base || `category_${Date.now()}`;
    }

    normalizeTabs(tabs) {
        const seen = new Set();
        const normalized = [];
        (Array.isArray(tabs) ? tabs : []).forEach(tab => {
            if (!tab || !tab.id) return;
            const id = String(tab.id);
            if (seen.has(id)) return;
            seen.add(id);
            normalized.push({
                id,
                name: String(tab.name || id)
            });
        });
        return normalized;
    }

    renderTabs() {
        const container = document.getElementById('tabsContainer');
        container.innerHTML = '';
        const tabCount = document.getElementById('tabCount');
        if (tabCount) {
            tabCount.textContent = `${this.tabs.length + 1} tabs`;
        }

        // Add Wishlist tab at the top
        const wishlistBtn = document.createElement('button');
        const wishlistCount = this.wishlist.size;
        wishlistBtn.className = `tab-btn wishlist-tab-btn ${this.currentTab === 'wishlist' ? 'active' : ''}`;
        wishlistBtn.innerHTML = `<span>♥ Wishlist</span><span class="count">${wishlistCount}</span>`;
        wishlistBtn.addEventListener('click', () => this.selectTab('wishlist'));
        container.appendChild(wishlistBtn);

        // Installed is a live machine view, not a user-editable category.
        const installedBtn = document.createElement('button');
        const installedCount = this.getTabCount('installed');
        installedBtn.type = 'button';
        installedBtn.className = `tab-btn installed-tab-btn ${this.currentTab === 'installed' ? 'active' : ''}`;
        installedBtn.innerHTML = `<span>🔴 Installed</span><span class="count">${installedCount}</span>`;
        installedBtn.title = 'Games detected in C:\\Games, F:\\Games, or E:\\Games';
        installedBtn.addEventListener('click', () => this.selectTab('installed'));
        container.appendChild(installedBtn);

        this.tabs.forEach(tab => {
            const isAdminOnly = this.isTabAdminOnly(tab.id);
            const isHidden = this.hiddenTabs.has(tab.id);

            // CRITICAL: Admin-only tabs are NEVER shown to non-admins
            if (isAdminOnly && !this.isAdmin) {
                return;
            }

            // Skip other hidden tabs for non-admins
            if (isHidden && !this.isAdmin) {
                return;
            }

            const count = this.getTabCount(tab.id);
            const btn = document.createElement('button');
            btn.className = `tab-btn ${tab.id === this.currentTab ? 'active' : ''} ${isHidden ? 'hidden-tab' : ''} ${isAdminOnly ? 'admin-only-tab' : ''}`;
            btn.type = 'button';

            // ONLY admins see visibility controls - NEVER show to non-admins
            if (this.isAdmin === true && tab.id !== 'all') {
                const safeName = this.escapeHtml(tab.name);
                if (isAdminOnly) {
                    // Admin-only tabs show lock icon (cannot be toggled, permanently admin-only)
                    btn.innerHTML = `
                        <span>${safeName}</span>
                        <span class="count">${count}</span>
                        <span class="admin-only-indicator" title="Admin-only tab (cannot be made public)">🔒</span>
                    `;
                } else {
                    // Regular tabs show visibility toggle
                    btn.innerHTML = `
                        <span>${safeName}</span>
                        <span class="count">${count}</span>
                        <span class="tab-admin-actions" aria-label="Admin category actions">
                            <span class="tab-visibility-toggle" data-tab="${this.escapeHtml(tab.id)}" title="${isHidden ? 'Show to all' : 'Hide from non-admins'}">${isHidden ? '👁️‍🗨️' : '👁️'}</span>
                            <span class="tab-rename-btn" data-tab="${this.escapeHtml(tab.id)}" title="Rename category">✎</span>
                            <span class="tab-delete-btn" data-tab="${this.escapeHtml(tab.id)}" title="Delete category">×</span>
                        </span>
                    `;
                }
            } else {
                btn.innerHTML = `
                    <span>${this.escapeHtml(tab.name)}</span>
                    <span class="count">${count}</span>
                `;
            }

            btn.addEventListener('click', (e) => {
                if (e.target.classList.contains('tab-visibility-toggle')) {
                    e.stopPropagation();
                    this.toggleTabVisibility(e.target.dataset.tab);
                } else if (e.target.classList.contains('tab-rename-btn')) {
                    e.stopPropagation();
                    this.renameCategory(e.target.dataset.tab);
                } else if (e.target.classList.contains('tab-delete-btn')) {
                    e.stopPropagation();
                    this.deleteCategory(e.target.dataset.tab);
                } else {
                    this.selectTab(tab.id);
                }
            });
            container.appendChild(btn);
        });
    }

    createCategoryFromInput(name) {
        if (!this.isAdmin) {
            this.showToast('Admin access required to create categories', 'error');
            return;
        }

        const cleanName = String(name || '').trim();
        if (!cleanName) {
            this.showToast('Enter a category name first', 'warning');
            return;
        }

        let id = this.createCategoryId(cleanName);
        const existingIds = new Set(this.tabs.map(tab => tab.id));
        const originalId = id;
        let suffix = 2;
        while (existingIds.has(id)) {
            id = `${originalId}_${suffix++}`;
        }

        this.tabs.push({ id, name: cleanName });
        this.currentTab = id;
        this.persistCategoryConfig(`Created category "${cleanName}"`);
    }

    renameCategory(tabId) {
        if (!this.isAdmin) return;
        if (tabId === 'all' || this.isTabAdminOnly(tabId)) {
            this.showToast('This category cannot be renamed', 'warning');
            return;
        }

        const tab = this.tabs.find(t => t.id === tabId);
        if (!tab) return;

        const nextName = prompt('Rename category:', tab.name);
        if (nextName === null) return;
        const cleanName = nextName.trim();
        if (!cleanName) {
            this.showToast('Category name cannot be empty', 'error');
            return;
        }

        tab.name = cleanName;
        this.persistCategoryConfig(`Renamed category to "${cleanName}"`);
    }

    deleteCategory(tabId) {
        if (!this.isAdmin) return;
        if (tabId === 'all' || this.isTabAdminOnly(tabId)) {
            this.showToast('This category cannot be deleted', 'warning');
            return;
        }

        const tab = this.tabs.find(t => t.id === tabId);
        if (!tab) return;

        const fallbackTabs = this.tabs.filter(t => t.id !== tabId && !this.isTabAdminOnly(t.id));
        const fallback = fallbackTabs.find(t => t.id === 'new') || fallbackTabs.find(t => t.id === 'action') || fallbackTabs.find(t => t.id !== 'all');
        if (!fallback) {
            this.showToast('Create another category before deleting this one', 'error');
            return;
        }

        const affectedCount = this.games.filter(game => game.category === tabId).length;
        const ok = confirm(`Delete "${tab.name}"? ${affectedCount} game(s) will move to "${fallback.name}". This saves permanently.`);
        if (!ok) return;

        this.games.forEach(game => {
            if (game.category === tabId) {
                game.category = fallback.id;
            }
        });
        this.tabs = this.tabs.filter(t => t.id !== tabId);
        this.hiddenTabs.delete(tabId);
        if (this.currentTab === tabId) {
            this.currentTab = fallback.id;
        }
        this.persistCategoryConfig(`Deleted "${tab.name}" and moved ${affectedCount} game(s)`);
    }

    persistCategoryConfig(successMessage) {
        localStorage.setItem('gameLibraryTabs', JSON.stringify(this.tabs));
        localStorage.setItem('gameLibraryGames', JSON.stringify(this.games));
        this.renderTabs();
        this.filterAndRender();
        this.updateSelectedCount();
        this.saveAdminConfigToServer();
        if (successMessage) {
            this.showToast(`${successMessage}. Saving permanently...`, 'success');
        }
    }

    getTabCount(tabId) {
        if (tabId === 'all') {
            return this.games.filter(g => this.isVisibleInAll(g)).length;
        }
        if (tabId === 'installed') {
            return this.getInstalledDisplayGames().length;
        }
        return this.games.filter(g => g.category === tabId).length;
    }

    isVisibleInAll(game) {
        if (!game) return false;
        return !this.ADMIN_ONLY_TABS.has(game.category) && !this.hiddenTabs.has(game.category);
    }

    selectTab(tabId) {
        // CRITICAL: Prevent non-admins from selecting admin-only tabs
        if (!this.isAdmin && this.isTabAdminOnly(tabId)) {
            this.showToast('Access denied: Admin-only tab', 'error');
            return;
        }
        this.currentTab = tabId;
        this.renderTabs();
        this.filterAndRender();
    }

    filterAndRender() {
        const searchQuery = this.searchQuery;
        const installedView = this.getInstalledDisplayGames();
        let filtered = searchQuery
            ? (this.currentTab === 'installed' ? installedView : [...this.games])
            : this.currentTab === 'all'
            ? this.games.filter(g => this.isVisibleInAll(g))
            : this.currentTab === 'wishlist'
            ? this.games.filter(g => this.wishlist.has(g.id))
            : this.currentTab === 'installed'
            ? installedView
            : this.games.filter(g => g.category === this.currentTab);

        // Hidden/admin categories must never leak into normal non-search browsing.
        if (!searchQuery && !this.isAdmin) {
            filtered = filtered.filter(g => !this.ADMIN_ONLY_TABS.has(g.category));
        }

        if (!searchQuery && !this.isAdmin && this.hiddenTabs.size > 0) {
            filtered = filtered.filter(g => !this.hiddenTabs.has(g.category));
        }

        if (searchQuery) {
            filtered = filtered.filter(g =>
                g.name.toLowerCase().includes(searchQuery) ||
                g.id.toLowerCase().includes(searchQuery) ||
                (g.category && g.category.toLowerCase().includes(searchQuery)) ||
                (g.dockerImage && g.dockerImage.toLowerCase().includes(searchQuery)) ||
                (g.dockerImageUrl && g.dockerImageUrl.toLowerCase().includes(searchQuery)) ||
                (g.localPath && g.localPath.toLowerCase().includes(searchQuery))
            );
        }

        // Filter by active tag
        if (this.activeTagFilter) {
            filtered = filtered.filter(g => {
                const tags = this.gameTags[g.id];
                return tags && tags.includes(this.activeTagFilter);
            });
        }

        // Filter by installed status if enabled
        if (this.showInstalledOnly && this.currentTab !== 'installed') {
            filtered = filtered.filter(g => this.installedGames.has(g.id));
        }

        // Filter by minimum star rating
        if (this.minRatingFilter > 0) {
            filtered = filtered.filter(g => this.getGameRating(g.id) >= this.minRatingFilter);
        }

        filtered.sort((a, b) => {
            let valA, valB;
            let hasA, hasB; // Track if values exist for proper fallback handling

            switch (this.sortBy) {
                case 'name':
                    valA = a.name.toLowerCase();
                    valB = b.name.toLowerCase();
                    hasA = hasB = true;
                    break;
                case 'time':
                    hasA = this.times[a.id] !== undefined && this.times[a.id] !== null;
                    hasB = this.times[b.id] !== undefined && this.times[b.id] !== null;
                    valA = hasA ? this.times[a.id] : null;
                    valB = hasB ? this.times[b.id] : null;
                    break;
                case 'category':
                    valA = a.category || 'zzz';
                    valB = b.category || 'zzz';
                    hasA = hasB = true;
                    break;
                case 'size':
                    hasA = this.imageSizes[a.id] !== undefined && this.imageSizes[a.id] !== null;
                    hasB = this.imageSizes[b.id] !== undefined && this.imageSizes[b.id] !== null;
                    valA = hasA ? this.imageSizes[a.id] : null;
                    valB = hasB ? this.imageSizes[b.id] : null;
                    break;
                case 'date':
                    // Use current timestamp for comparison so "new" category games appear first when sorting newest-to-oldest
                    const nowTimestamp = Date.now();
                    
                    valA = this.getAddedTimestamp(a);
                    valB = this.getAddedTimestamp(b);
                    hasA = valA !== null;
                    hasB = valB !== null;
                    
                    // Games with "new" category but no date should be treated as newest (Date.now())
                    if (a.category === 'new' && !hasA) {
                        valA = nowTimestamp;
                        hasA = true;
                    }
                    if (b.category === 'new' && !hasB) {
                        valB = nowTimestamp;
                        hasB = true;
                    }
                    break;
                case 'rating':
                    valA = this.getGameRating(a.id);
                    valB = this.getGameRating(b.id);
                    hasA = valA > 0;
                    hasB = valB > 0;
                    break;
                default:
                    valA = a.name.toLowerCase();
                    valB = b.name.toLowerCase();
                    hasA = hasB = true;
            }

            // Items without values always go to the end, regardless of sort order
            if (!hasA && !hasB) {
                // Both missing: sort by name for stability
                return a.name.toLowerCase().localeCompare(b.name.toLowerCase());
            }
            if (!hasA) return 1;  // a goes to end
            if (!hasB) return -1; // b goes to end

            // Normal comparison for items with values
            if (valA < valB) return this.sortOrder === 'asc' ? -1 : 1;
            if (valA > valB) return this.sortOrder === 'asc' ? 1 : -1;

            // Equal values: sort by name for stability
            return a.name.toLowerCase().localeCompare(b.name.toLowerCase());
        });

        this.filteredGames = filtered;
        document.getElementById('filteredCount').textContent = filtered.length;

        this.renderGames();
    }

    renderGames() {
        const grid = document.getElementById('gamesGrid');
        const noResults = document.getElementById('noResults');

        if (this.filteredGames.length === 0) {
            grid.innerHTML = '';
            noResults.style.display = 'block';
            return;
        }

        noResults.style.display = 'none';
        grid.innerHTML = this.filteredGames.map(game => this.createGameCard(game)).join('');

        // Add click handlers for info button
        grid.querySelectorAll('.game-card').forEach(card => {
            const infoBtn = card.querySelector('.info-btn');
            const installBtn = card.querySelector('.install-btn');
            const moveBtn = card.querySelector('.admin-card-move-inline');
            const checkbox = card.querySelector('.select-checkbox');

            // Info button opens modal
            infoBtn.addEventListener('click', (e) => {
                e.stopPropagation();
                const gameId = card.dataset.id;
                const game = this.games.find(g => g.id === gameId);
                if (game) this.openGameModal(game);
            });

            // Install button toggles installed status
            installBtn.addEventListener('click', (e) => {
                e.stopPropagation();
                const gameId = card.dataset.id;
                this.toggleInstalled(gameId);
            });

            if (moveBtn) {
                moveBtn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    const gameId = card.dataset.id;
                    this.openCategoryMovePanel([gameId], 'single-card');
                });
            }

            // Checkbox toggles selection
            checkbox.addEventListener('change', (e) => {
                e.stopPropagation();
                const gameId = card.dataset.id;
                this.toggleGameSelection(gameId, e.target.checked);
            });

            // Card tag chip — click to filter by that tag
            card.querySelectorAll('.card-tag-chip').forEach(chip => {
                chip.addEventListener('click', (e) => {
                    e.stopPropagation();
                    this.setActiveTagFilter(chip.dataset.tag);
                });
            });

            // Wishlist button toggles wishlist
            const wishlistBtn = card.querySelector('.wishlist-btn');
            if (wishlistBtn) {
                wishlistBtn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    const gameId = card.dataset.id;
                    this.toggleWishlist(gameId);
                });
            }

            // Star rating clicks
            card.querySelectorAll('.star-btn').forEach(star => {
                star.addEventListener('click', (e) => {
                    e.stopPropagation();
                    const gameId = card.dataset.id;
                    const newRating = parseInt(e.target.dataset.star);
                    const cur = this.getGameRating(gameId);
                    this.setRating(gameId, cur === newRating ? 0 : newRating);
                });
            });

            // Card click toggles selection
            card.addEventListener('click', (e) => {
                if (e.target !== checkbox && !e.target.closest('.info-btn') && !e.target.closest('.install-btn') && !e.target.closest('.admin-card-move-inline') && !e.target.closest('.card-tag-chip') && !e.target.closest('.wishlist-btn') && !e.target.closest('.rating-stars')) {
                    const gameId = card.dataset.id;
                    checkbox.checked = !checkbox.checked;
                    this.toggleGameSelection(gameId, checkbox.checked);
                }
            });

            card.addEventListener('keydown', (e) => {
                if ((e.key === 'Enter' || e.key === ' ') && e.target === card) {
                    e.preventDefault();
                    const gameId = card.dataset.id;
                    checkbox.checked = !checkbox.checked;
                    this.toggleGameSelection(gameId, checkbox.checked);
                }
            });
        });

        this.lazyLoadImages();
    }

    createGameCard(game) {
        // Use time from game entry (games.json) first, fall back to times.json lookup
        const time = this.getGameTime(game);
        const hasTime = time != null && time !== '';
        const timeStr = hasTime ? `${time}h` : 'N/A';
        const size = this.imageSizes[game.id];
        const hasSize = size != null && size !== '';
        const sizeStr = hasSize ? `${size} GB` : 'N/A';
        // Use image path from game entry (games.json) first, fall back to derived path
        const imageSrc = this.getDisplayImageUrl(game.image || `images/${game.id.toLowerCase()}.png`);
        const fallbackImageSrc = this.createGeneratedCoverDataUrl(game.id);
        const dockerImageUrl = game.localPath ? null : (game.dockerImageUrl || this.getDockerHubTagUrl(game.id, this.settings.dockerUsername, this.settings.repoName));
        const isSelected = this.selectedGames.has(game.id);
        const isLocalInstalled = !!game.isLocalInstalled;
        const isInstalled = isLocalInstalled || this.installedGames.has(game.id);
        const isNew = game.category === 'new';
        const isWishlisted = this.wishlist.has(game.id);
        const gameRating = this.getGameRating(game.id);
        const ratingStars = [1,2,3,4,5].map(s => `<button type="button" class="star-btn ${s <= gameRating ? 'star-filled' : ''}" data-star="${s}" title="Rate ${s} star${s>1?'s':''}" aria-label="Rate ${game.name} ${s} star${s>1?'s':''}" aria-pressed="${s <= gameRating}">&#9733;</button>`).join('');

        return `
            <div class="game-card ${isSelected ? 'selected' : ''} ${isInstalled ? 'installed' : ''} ${isNew ? 'new-game' : ''}" data-id="${game.id}" role="article" tabindex="0" aria-label="${game.name}. Press Enter or Space to select.">
                <input type="checkbox" class="select-checkbox" ${isSelected ? 'checked' : ''} aria-label="Select ${game.name}">
                ${isSelected ? '<span class="checkmark-icon">✓</span>' : ''}
                <button type="button" class="info-btn" title="View details" aria-label="View details for ${game.name}">ℹ️</button>
                <button type="button" class="install-btn ${isInstalled ? 'is-installed' : ''}" ${isLocalInstalled ? 'disabled' : ''} title="${isLocalInstalled ? 'Detected by the local installed-game scanner' : (isInstalled ? 'Mark as not installed' : 'Mark as installed')}" aria-label="${isLocalInstalled ? 'Detected by the local installed-game scanner' : (isInstalled ? 'Mark as not installed' : 'Mark as installed')}" aria-pressed="${isInstalled}">${isInstalled ? '✅' : '📥'}</button>
                <button type="button" class="youtube-btn" title="Watch trailer on YouTube" aria-label="Watch ${game.name} trailer on YouTube" onclick="window.open('https://www.youtube.com/results?search_query=${encodeURIComponent(game.name + ' trailer')}', '_blank', 'noopener')">▶</button>
                ${isNew ? '<div class="new-badge">🆕 NEW</div>' : ''}
                ${isInstalled ? '<div class="installed-badge">✓ Installed</div>' : ''}
                ${isWishlisted ? '<div class="wishlist-badge">&#9829;</div>' : ''}
                <button type="button" class="wishlist-btn ${isWishlisted ? 'wishlisted' : ''}" title="${isWishlisted ? 'Remove from wishlist' : 'Add to wishlist'}" aria-label="${isWishlisted ? 'Remove from wishlist' : 'Add to wishlist'}" aria-pressed="${isWishlisted}">&#9829;</button>
                <p class="image-container">
                    <img
                        data-src="${imageSrc}"
                        src="${imageSrc}"
                        alt="${game.name}"
                        loading="lazy"
                        onerror="this.onerror=null;this.src='${fallbackImageSrc}'"
                    >
                </p>
                <div class="card-info">
                    <div class="title" title="${game.name}">${game.name}</div>
                    ${game.localPath ? `<div class="local-install-path" title="${this.escapeHtml(game.localPath)}">📍 ${this.escapeHtml(game.localPath)}</div>` : ''}
                    <div class="meta">
                        ${this.settings.showCategories ? `<span class="category-badge">${game.category || 'uncategorized'}</span>` : ''}
                        ${this.settings.showTimes ? `<span class="time-badge" title="${hasTime ? `~${time} hours to complete` : 'No time data'}">⏱️ ${timeStr}</span>` : ''}
                        <span class="size-badge">💾 ${sizeStr}</span>
                        ${dockerImageUrl ? `<a class="docker-badge" href="${dockerImageUrl}" target="_blank" title="View on Docker Hub" rel="noopener">🐳</a>` : ''}
                    </div>
                    ${this._renderCardTags(game.id)}
                    <button type="button" class="admin-card-move-inline admin-only" aria-label="Move ${game.name} to another category">↪ Move category</button>
                    <div class="rating-stars" data-gameid="${game.id}" title="Rate this game">${ratingStars}</div>
                </div>
            </div>
        `;
    }

    toggleGameSelection(gameId, isSelected) {
        if (isSelected) {
            this.selectedGames.add(gameId);
        } else {
            this.selectedGames.delete(gameId);
        }

        // Update card visual
        const card = document.querySelector(`.game-card[data-id="${gameId}"]`);
        if (card) {
            card.classList.toggle('selected', isSelected);
        }

        this.updateSelectedCount();
    }

    updateSelectedCount() {
        const count = this.selectedGames.size;
        document.getElementById('selectedCount').textContent = count;

        const runBtn = document.getElementById('runSelectedBtn');
        runBtn.textContent = `▶️ Run Selected (${count})`;
        runBtn.disabled = count === 0;

        // Enable/disable the format dropdown button for Run Selected
        const runFormatBtn = document.getElementById('runFormatBtn');
        if (runFormatBtn) {
            runFormatBtn.disabled = count === 0;
        }

        const moveToBtn = document.getElementById('moveToBtn');
        const moveToContainer = document.querySelector('.move-to-container');

        // Hide Move To button for non-admins
        if (moveToContainer) {
            moveToContainer.style.display = this.isAdmin ? 'block' : 'none';
        }

        if (moveToBtn) {
            moveToBtn.textContent = count > 0 ? `📁 Move Selected (${count})` : '📁 Move Selected';
            moveToBtn.title = count > 0 ? 'Move selected games to a permanent category' : 'Select games first, or use the Move button on a card';
            moveToBtn.disabled = count === 0 || !this.isAdmin;
        }
    }


    updateStatsDashboard() {
        const total = this.games.length;
        const installed = this.installedGames.size;
        const pct = total > 0 ? Math.round((installed / total) * 100) : 0;

        // Average HLTB time from times data
        const timeValues = Object.values(this.times).filter(function(v) {
            return v !== null && v !== undefined && !isNaN(Number(v));
        });
        let avgTimeStr = '\u2014';
        if (timeValues.length > 0) {
            const avg = timeValues.reduce(function(s, v) { return s + Number(v); }, 0) / timeValues.length;
            avgTimeStr = avg >= 1 ? ('~' + avg.toFixed(1) + 'h') : ('~' + Math.round(avg * 60) + 'm');
        }

        // Games with cover images: games present in imageSizes
        const imgKeys = Object.keys(this.imageSizes).map(function(k) { return k.toLowerCase(); });
        const gamesWithImages = this.games.filter(function(g) {
            return imgKeys.indexOf(g.id.toLowerCase()) !== -1;
        }).length;

        const elById = function(id) { return document.getElementById(id); };
        if (elById('statsTotalGames')) elById('statsTotalGames').textContent = total;
        if (elById('statsInstalled')) {
            elById('statsInstalled').innerHTML = installed + ' <span class="stats-pct">(' + pct + '%)</span>';
        }
        if (elById('statsAvgTime')) elById('statsAvgTime').textContent = avgTimeStr;
        if (elById('statsWithImages')) elById('statsWithImages').textContent = gamesWithImages;
    }
    selectAllVisible() {
        this.filteredGames.forEach(game => {
            this.selectedGames.add(game.id);
        });
        this.filterAndRender();
        this.updateSelectedCount();
        this.showToast(`Selected ${this.filteredGames.length} games`, 'success');
    }

    deselectAll() {
        this.selectedGames.clear();
        this.filterAndRender();
        this.updateSelectedCount();
        this.showToast('All games deselected', 'info');
    }

    getDockerCommand(gameId) {
        const dockerUser = this.settings.dockerUsername || 'michadockermisha';
        const repoName = this.settings.repoName || 'backup';
        const mountPath = document.getElementById('globalMountPath').value || this.settings.mountPath || 'F:/Games';

        // Normalize path for Docker
        const normalizedPath = mountPath.replace(/\\/g, '/');

        // Simple docker command - mount user's folder to /output, copy game files there
        return `docker run -v "${normalizedPath}:/output" --rm --name ${gameId} ${dockerUser}/${repoName}:${gameId} sh -c "mkdir -p /output/${gameId} && cp -rv /home/* /output/${gameId}/"`;
    }

    openGameModal(game) {
        const modal = document.getElementById('gameModal');
        const time = this.getGameTime(game);
        const size = this.imageSizes[game.id];

        document.getElementById('modalTitle').textContent = game.name;
        document.getElementById('modalCategory').textContent = game.category || 'uncategorized';
        document.getElementById('modalTime').textContent = (time != null && time !== '') ? `~${time} hours` : 'N/A';
        document.getElementById('modalSize').textContent = (size != null && size !== '') ? `${size} GB` : 'N/A';
        const modalDescription = document.getElementById('modalDescription');
        if (modalDescription) {
            modalDescription.textContent = game.details || game.description || '';
        }
        const modalImg = document.getElementById('modalImage');
        const gameName = game.name;
        modalImg.src = this.getDisplayImageUrl(game.image || this.createGeneratedCoverDataUrl(game.id));
        modalImg.onerror = async function() {
            // Try Steam cover fallback
            try {
                const steamUrl = await window.gameLibrary.fetchSteamCoverUrl(gameName);
                if (steamUrl) {
                    modalImg.src = window.gameLibrary.getDisplayImageUrl(steamUrl);
                    modalImg.onerror = function() {
                        this.src = "data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 300 400'%3E%3Crect fill='%231f2937' width='300' height='400'/%3E%3Ctext x='150' y='200' text-anchor='middle' fill='%236366f1' font-size='40'%3E🎮%3C/text%3E%3C/svg%3E";
                    };
                    return;
                }
            } catch (e) { /* fallback to placeholder */ }
            this.src = "data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 300 400'%3E%3Crect fill='%231f2937' width='300' height='400'/%3E%3Ctext x='150' y='200' text-anchor='middle' fill='%236366f1' font-size='40'%3E🎮%3C/text%3E%3C/svg%3E";
        };

        const dockerCmd = this.getDockerCommand(game.id);
        document.getElementById('dockerCommand').textContent = dockerCmd;

        // Render tag section in modal
        this._renderModalTagSection(game);

        this.currentGame = game;
        modal.classList.add('active');
    }

    _renderModalTagSection(game) {
        let tagSection = document.getElementById('modalTagSection');
        if (!tagSection) {
            const modalInfo = document.querySelector('.modal-info');
            if (!modalInfo) return;
            tagSection = document.createElement('div');
            tagSection.id = 'modalTagSection';
            tagSection.className = 'modal-tag-section';
            modalInfo.appendChild(tagSection);
        }
        const currentTags = this.getGameTags(game.id);
        tagSection.innerHTML = `
            <div class="modal-tag-header">
                <span class="modal-tag-label">\uD83C\uDFF7\uFE0F Tags</span>
                <button class="modal-tag-edit-btn" id="modalTagEditBtn">Edit Tags</button>
            </div>
            <div class="modal-tag-chips" id="modalTagChips">
                ${currentTags.length
                    ? currentTags.map(t => `<span class="modal-tag-chip" data-tag="${t}">${t}</span>`).join('')
                    : '<span class="modal-tag-none">No tags assigned</span>'
                }
            </div>
        `;
        document.getElementById('modalTagEditBtn').addEventListener('click', () => {
            this.closeModal('gameModal');
            this.openTagEditor(game.id);
        });
        tagSection.querySelectorAll('.modal-tag-chip').forEach(chip => {
            chip.addEventListener('click', () => {
                this.closeModal('gameModal');
                this.setActiveTagFilter(chip.dataset.tag);
            });
        });
    }

    closeModal(modalId) {
        const modal = document.getElementById(modalId);
        if (modal) {
            modal.classList.remove('active');
            modal.setAttribute('aria-hidden', 'true');
        }
    }

    closeAllModals() {
        document.querySelectorAll('.modal').forEach(m => {
            m.classList.remove('active');
            m.setAttribute('aria-hidden', 'true');
        });
        document.getElementById('sortMenu').style.display = 'none';
    }

    copyToClipboard() {
        const command = document.getElementById('dockerCommand').textContent;
        navigator.clipboard.writeText(command).then(() => {
            this.showToast('Command copied to clipboard!', 'success');
        }).catch(() => {
            this.showToast('Failed to copy', 'error');
        });
    }

    runInTerminal(format = 'bat') {
        if (!this.currentGame) return;
        this.downloadRunScript([this.currentGame.id], format);
    }

    runSelectedGames() {
        if (this.selectedGames.size === 0) {
            this.showToast('No games selected', 'error');
            return;
        }
        this.downloadRunScript([...this.selectedGames]);
    }

    escapeBatchText(value) {
        return String(value ?? '')
            .replace(/\^/g, '^^')
            .replace(/%/g, '%%')
            .replace(/!/g, '^^!')
            .replace(/&/g, '^&')
            .replace(/\|/g, '^|')
            .replace(/</g, '^<')
            .replace(/>/g, '^>')
            .replace(/\(/g, '^(')
            .replace(/\)/g, '^)');
    }

    downloadRunScript(gameIds, format = 'bat') {
        const dockerUser = this.settings.dockerUsername || 'michadockermisha';
        const repoName = this.settings.repoName || 'backup';
        const mountPath = document.getElementById('globalMountPath').value || this.settings.mountPath || 'F:/Games';

        // Normalize the mount path for Docker on Windows
        // Docker Desktop accepts paths like "E:/Games" or "E:\Games"
        // We normalize to forward slashes for consistency
        const normalizedPath = mountPath.replace(/\\/g, '/');

        // For Windows Docker, we mount the user's chosen folder directly to /output inside container
        // This is simpler and more reliable than mounting the entire drive
        const dockerVolume = `"${normalizedPath}:/output"`;

        let script, filename;
        const timestamp = new Date().toISOString().replace(/[:.]/g, '-');
        const gameCount = gameIds.length;

        if (this.os === 'windows' && format === 'bat') {
            // Windows Batch script (.bat) - double-click to run!
            const gameCommands = gameIds.map((id, idx) => {
                const game = this.games.find(g => g.id === id);
                const gameName = game ? game.name : id;
                const safeGameName = this.escapeBatchText(gameName);
                const safeId = this.escapeBatchText(id);
                return `call :process_game "${safeId}" "${safeGameName}" "${idx + 1}" "${gameCount}"`;
            }).join('\n');

            script = `@echo off
setlocal EnableDelayedExpansion
REM ============================================================
REM Game Library Manager - Docker Runner (Batch)
REM Generated: ${new Date().toISOString()}
REM Games: ${gameCount}
REM ============================================================
REM
REM INSTRUCTIONS:
REM 1. Make sure Docker Desktop is running
REM 2. Double-click this .bat file to run
REM 3. Games will be downloaded to ${mountPath}
REM
REM Each game is fully processed (pull + extract) before moving
REM to the next one, so you can see real-time progress.
REM
REM ============================================================

echo.
echo  ====================================
echo   Game Library Manager v5.0
echo   Processing ${gameCount} game(s)
echo   Destination: ${mountPath}
echo  ====================================
echo.

REM Initial Docker health check with recovery
echo Checking Docker status...
docker info >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo [WARNING] Docker is not responding, attempting to start/restart...
    call :recover_docker
    docker info >nul 2>&1
    if !ERRORLEVEL! NEQ 0 (
        echo [ERROR] Docker is not running! Please start Docker Desktop manually.
        exit /b 1
    )
)

echo [OK] Docker is running
echo.

REM Test network connectivity first
echo Testing network connectivity...
ping -n 1 registry-1.docker.io >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo [WARNING] Ping cannot reach Docker Hub. This can be normal when ICMP is blocked.
    echo [WARNING] Continuing immediately; Docker pull will verify real connectivity.
    ipconfig /flushdns >nul 2>&1
)

echo.
echo ============================================================
echo Starting game processing...
echo Each game: Pull image -^> Extract files -^> Next game
echo This script exits with an error if any selected game fails.
echo ============================================================

set /a SUCCESS_COUNT=0
set /a FAIL_COUNT=0
set "FAILED_GAMES="

${gameCommands}

echo.
echo ############################################################
echo  FINAL SUMMARY
echo  Successful: !SUCCESS_COUNT! / ${gameCount}
echo  Failed: !FAIL_COUNT! / ${gameCount}
if !FAIL_COUNT! GTR 0 echo  Failed games: !FAILED_GAMES!
if !FAIL_COUNT! EQU 0 echo  All selected games extracted and verified in ${mountPath}.
echo ############################################################
echo.
if !FAIL_COUNT! GTR 0 (
    echo [FAILED] One or more selected games did not extract successfully.
    exit /b 1
)
goto :end

REM ============================================================
REM Per-game processor
REM ============================================================
:process_game
set "GAME_ID=%~1"
set "GAME_NAME=%~2"
set "GAME_INDEX=%~3"
set "GAME_TOTAL=%~4"

echo.
echo ############################################################
echo  GAME !GAME_INDEX!/!GAME_TOTAL!: !GAME_NAME!
echo ############################################################
echo [%date% %time%] Starting...
echo.

REM ============================================================
REM STEP 1: Pull the Docker image
REM ============================================================
echo [STEP 1/2] Pulling Docker image for !GAME_NAME!...
set "PULL_SUCCESS=0"
for /L %%i in (1,1,5) do (
    if !PULL_SUCCESS! EQU 0 (
        docker info >nul 2>&1
        if !ERRORLEVEL! NEQ 0 (
            echo [WARNING] Docker not responding, attempting recovery...
            call :recover_docker
        )

        docker pull ${dockerUser}/${repoName}:!GAME_ID!
        if !ERRORLEVEL! EQU 0 (
            set "PULL_SUCCESS=1"
            echo [OK] Image pulled successfully!
        ) else (
            docker image inspect ${dockerUser}/${repoName}:!GAME_ID! >nul 2>&1
            if !ERRORLEVEL! EQU 0 (
                set "PULL_SUCCESS=1"
                echo [OK] Pull failed, but image already exists locally. Continuing with local image.
            )
        )

        if !PULL_SUCCESS! EQU 0 (
            echo [RETRY %%i/5] Pull failed, retrying immediately...
            ipconfig /flushdns >nul 2>&1
        )
    )
)

if !PULL_SUCCESS! EQU 0 (
    echo [ERROR] Failed to pull !GAME_NAME! after 5 attempts.
    call :mark_failed "!GAME_NAME!" "pull failed"
    goto :eof
)

REM ============================================================
REM STEP 2: Extract game files to destination
REM ============================================================
echo.
echo [STEP 2/2] Extracting files to: ${normalizedPath}\\!GAME_ID!
echo.

REM Clean up any existing container with same name
docker stop !GAME_ID! >nul 2>&1
docker rm -f !GAME_ID! >nul 2>&1

set "RUN_SUCCESS=0"
for /L %%a in (1,1,3) do (
    if !RUN_SUCCESS! EQU 0 (
        echo [ATTEMPT %%a/3] Running extraction container...
        echo.

        REM Extract from a stopped container with docker cp. This does not require sh/bash inside the game image.
        set "IMAGE_REF=${dockerUser}/${repoName}:!GAME_ID!"
        set "GLM_DEST_ROOT=${normalizedPath}"
        powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; if (Get-Variable PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) { $PSNativeCommandUseErrorActionPreference=$false }; $id=$env:GAME_ID; $name=$env:GAME_NAME; $image=$env:IMAGE_REF; $root=$env:GLM_DEST_ROOT; $container=('glm_extract_' + ($id -replace '[^A-Za-z0-9_.-]','_')); $tmp=Join-Path $root ('.glm-extracting-' + $id); $dst=Join-Path $root $id; Write-Host '=== CONTAINER CREATED FOR COPY ==='; cmd.exe /c ('docker rm -f \"' + $container + '\" >nul 2>nul'); try { if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Recurse -Force }; New-Item -ItemType Directory -Force -Path $tmp | Out-Null; docker create --name $container $image /__glm_copy_only__ | Out-Null; if ($LASTEXITCODE -ne 0) { throw ('docker create failed for ' + $image) }; Write-Host ('Copying /home from image to temporary folder: ' + $tmp); $copy = Start-Process -FilePath 'docker' -ArgumentList @('cp', ($container + ':/home/.'), $tmp) -PassThru -WindowStyle Hidden; while (-not $copy.HasExited) { Start-Sleep -Seconds 2; $m = Get-ChildItem -LiteralPath $tmp -Force -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum; Write-Host ('[PROGRESS] Extracting ' + $name + ': ' + $m.Count + ' files, ' + ([math]::Round(($m.Sum / 1GB), 2)) + ' GB copied'); }; $copy.WaitForExit(); if ($copy.ExitCode -ne 0) { throw ('docker cp failed with exit ' + $copy.ExitCode) }; $m = Get-ChildItem -LiteralPath $tmp -Force -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum; if ($m.Count -lt 1 -or $m.Sum -lt 1) { throw 'Extracted folder is empty' }; if (Test-Path -LiteralPath $dst) { Remove-Item -LiteralPath $dst -Recurse -Force }; Move-Item -LiteralPath $tmp -Destination $dst; Write-Host '=== COPY COMPLETE ==='; Write-Host ('[PROGRESS] Final extracted size: ' + $m.Count + ' files, ' + ([math]::Round(($m.Sum / 1GB), 2)) + ' GB') } finally { cmd.exe /c ('docker rm -f \"' + $container + '\" >nul 2>nul') }"

        if !ERRORLEVEL! EQU 0 (
            dir /a /b "${normalizedPath}\\!GAME_ID!" >nul 2>&1
            if !ERRORLEVEL! EQU 0 (
                >"${normalizedPath}\\!GAME_ID!\\.glm-extract-ok" echo !GAME_NAME! extracted by Game Library Manager on %date% %time%
                set "RUN_SUCCESS=1"
                set /a SUCCESS_COUNT+=1
                echo.
                echo ============================================================
                echo [SUCCESS] !GAME_NAME! extracted and verified successfully!
                echo [SAVED TO] ${normalizedPath}\\!GAME_ID!
                echo ============================================================
            ) else (
                echo.
                echo [WARNING] Docker exited successfully but destination folder is missing or empty.
            )
        ) else (
            echo.
            echo [WARNING] Attempt %%a/3 failed with error code !ERRORLEVEL!
            docker stop !GAME_ID! >nul 2>&1
            docker rm -f !GAME_ID! >nul 2>&1
            if %%a LSS 3 (
                echo [INFO] Retrying extraction immediately...
                docker info >nul 2>&1
                if !ERRORLEVEL! NEQ 0 call :recover_docker
            )
        )
    )
)

if !RUN_SUCCESS! EQU 0 (
    echo.
    echo [ERROR] !GAME_NAME! extraction FAILED after 3 attempts!
    call :mark_failed "!GAME_NAME!" "extract failed"
) else (
    if !GAME_INDEX! LSS !GAME_TOTAL! (
        echo.
        echo [NEXT] Moving to next game immediately...
    )
)
goto :eof

REM ============================================================
REM Failure tracking
REM ============================================================
:mark_failed
set /a FAIL_COUNT+=1
set "FAILED_GAMES=!FAILED_GAMES! %~1 (%~2);"
goto :eof

REM ============================================================
REM Docker Recovery Function
REM Handles Docker Desktop pipe errors and connection issues
REM ============================================================
:recover_docker
echo [RECOVERY] Attempting Docker Desktop recovery...

REM First, try to restart the Docker service
echo [RECOVERY] Restarting Docker service...
net stop com.docker.service >nul 2>&1
net start com.docker.service >nul 2>&1

REM Check if Docker is responding now
docker info >nul 2>&1
if !ERRORLEVEL! EQU 0 (
    echo [RECOVERY] Docker service restart successful!
    goto :eof
)

REM If service restart didn't work, try killing and restarting Docker Desktop
echo [RECOVERY] Restarting Docker Desktop application...
taskkill /f /im "Docker Desktop.exe" >nul 2>&1
taskkill /f /im "com.docker.backend.exe" >nul 2>&1
taskkill /f /im "com.docker.proxy.exe" >nul 2>&1

REM Try to start Docker Desktop
start "" "C:\\Program Files\\Docker\\Docker\\Docker Desktop.exe" >nul 2>&1
if !ERRORLEVEL! NEQ 0 (
    start "" "%PROGRAMFILES%\\Docker\\Docker\\Docker Desktop.exe" >nul 2>&1
)

echo [RECOVERY] Checking Docker readiness without delay...
set "DOCKER_READY=0"
for /L %%w in (1,1,12) do (
    if !DOCKER_READY! EQU 0 (
        docker info >nul 2>&1
        if !ERRORLEVEL! EQU 0 (
            set "DOCKER_READY=1"
            echo [RECOVERY] Docker Desktop is ready!
        ) else (
            echo [RECOVERY] Not ready yet... %%w/12
        )
    )
)

if !DOCKER_READY! EQU 0 (
    echo [RECOVERY] Docker Desktop recovery may have failed, will continue trying...
)
goto :eof

:glm_wait
set "GLM_WAIT_SECONDS=%~1"
if "%GLM_WAIT_SECONDS%"=="" set "GLM_WAIT_SECONDS=1"
if exist "%SystemRoot%\\System32\\timeout.exe" (
    "%SystemRoot%\\System32\\timeout.exe" /t %GLM_WAIT_SECONDS% /nobreak >nul 2>nul
    goto :eof
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Sleep -Seconds ([int]$env:GLM_WAIT_SECONDS)" >nul 2>nul
if !ERRORLEVEL! EQU 0 goto :eof
set /a "GLM_PING_COUNT=GLM_WAIT_SECONDS+1" >nul 2>nul
ping 127.0.0.1 -n !GLM_PING_COUNT! >nul 2>nul
goto :eof

:end
endlocal
`;
            filename = gameCount === 1
                ? `run_${gameIds[0]}.bat`
                : `run_${gameCount}_games_${timestamp}.bat`;

        } else if (this.os === 'windows') {
            // Windows PowerShell script (.ps1) - runs natively in PowerShell
            // Build combined pull+extract commands for each game (single-phase approach)
            const gameCommands = gameIds.map((id, idx) => {
                const game = this.games.find(g => g.id === id);
                const gameName = game ? game.name : id;
                const isLastGame = idx === gameCount - 1;
                return `
Write-Host ""
Write-Host "############################################################" -ForegroundColor Cyan
Write-Host " GAME ${idx + 1}/${gameCount}: ${gameName}" -ForegroundColor Cyan
Write-Host "############################################################" -ForegroundColor Cyan
Write-Host "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] Starting..."
Write-Host ""

# ============================================================
# STEP 1: Pull the Docker image
# ============================================================
Write-Host "[STEP 1/2] Pulling Docker image..." -ForegroundColor Yellow
\$pullSuccess = \$false
\$retryDelay = 5
for (\$i = 1; \$i -le 5 -and -not \$pullSuccess; \$i++) {
    docker info 2>\$null | Out-Null
    if (\$LASTEXITCODE -ne 0) {
        Write-Host "[WARNING] Docker not responding, waiting 10 seconds..." -ForegroundColor Yellow
        Start-Sleep -Seconds 10
    }

    docker pull ${dockerUser}/${repoName}:${id}
    if (\$LASTEXITCODE -eq 0) {
        \$pullSuccess = \$true
        Write-Host "[OK] Image pulled successfully!" -ForegroundColor Green
    } else {
        Write-Host "[RETRY \$i/5] Pull failed, retrying in \$retryDelay seconds..." -ForegroundColor Yellow
        Start-Sleep -Seconds \$retryDelay
        \$retryDelay = [Math]::Min(\$retryDelay * 2, 60)
    }
}

if (-not \$pullSuccess) {
    Write-Host "[ERROR] Failed to pull ${gameName} after 5 attempts. Skipping..." -ForegroundColor Red
    continue
}

# ============================================================
# STEP 2: Extract game files to destination
# ============================================================
Write-Host ""
Write-Host "[STEP 2/2] Extracting files to: ${normalizedPath}\\${id}" -ForegroundColor Yellow
Write-Host ""

# Clean up any existing container
docker stop ${id} 2>\$null | Out-Null
docker rm -f ${id} 2>\$null | Out-Null

\$runSuccess = \$false
for (\$attempt = 1; \$attempt -le 3 -and -not \$runSuccess; \$attempt++) {
    Write-Host "[ATTEMPT \$attempt/3] Running extraction container..." -ForegroundColor Gray
    Write-Host ""

    # Run container: mount user's folder to /output, copy game files there
    docker run -v ${dockerVolume} --rm --name ${id} ${dockerUser}/${repoName}:${id} sh -c "echo '=== CONTAINER STARTED ===' && echo 'Copying game files to /output/${id}...' && mkdir -p /output/${id} && cp -rv /home/* /output/${id}/ 2>&1 && echo '' && echo '=== COPY COMPLETE ===' && ls -la /output/${id}/ && echo '' && echo 'Total size:' && du -sh /output/${id}/"

    if (\$LASTEXITCODE -eq 0) {
        \$runSuccess = \$true
        Write-Host ""
        Write-Host "============================================================" -ForegroundColor Green
        Write-Host "[SUCCESS] ${gameName} extracted successfully!" -ForegroundColor Green
        Write-Host "[SAVED TO] ${normalizedPath}\\${id}" -ForegroundColor Green
        Write-Host "============================================================" -ForegroundColor Green
    } else {
        Write-Host ""
        Write-Host "[WARNING] Attempt \$attempt/3 failed with exit code \$LASTEXITCODE" -ForegroundColor Yellow
        docker stop ${id} 2>\$null | Out-Null
        docker rm -f ${id} 2>\$null | Out-Null
        if (\$attempt -lt 3) {
            Write-Host "[INFO] Waiting 10 seconds before retry..." -ForegroundColor Gray
            Start-Sleep -Seconds 10
        }
    }
}

if (-not \$runSuccess) {
    Write-Host ""
    Write-Host "[ERROR] ${gameName} extraction FAILED after 3 attempts!" -ForegroundColor Red
    Write-Host "[ERROR] Check Docker status and try again." -ForegroundColor Red
}`;
            }).join('\n');

            script = `# ============================================================
# Game Library Manager - Docker Runner (PowerShell)
# Generated: ${new Date().toISOString()}
# Games: ${gameCount}
# ============================================================
#
# INSTRUCTIONS:
# 1. Make sure Docker Desktop is running
# 2. Run: .\\${gameCount === 1 ? `run_${gameIds[0]}.ps1` : `run_${gameCount}_games.ps1`}
# 3. Games will be downloaded to ${mountPath}
#
# Each game is fully processed (pull + extract) before moving
# to the next one, so you can see real-time progress.
#
# If you get execution policy error, run:
#   Set-ExecutionPolicy -ExecutionPolicy Bypass -Scope Process
#
# ============================================================

Write-Host ""
Write-Host "  ====================================" -ForegroundColor Cyan
Write-Host "   Game Library Manager v5.0"
Write-Host "   Processing ${gameCount} game(s)"
Write-Host "   Destination: ${mountPath}"
Write-Host "  ====================================" -ForegroundColor Cyan
Write-Host ""

# Check if Docker is running
Write-Host "Checking Docker status..."
docker info 2>\$null | Out-Null
if (\$LASTEXITCODE -ne 0) {
    Write-Host "[ERROR] Docker is not running! Please start Docker Desktop and try again." -ForegroundColor Red
    exit 1
}
Write-Host "[OK] Docker is running" -ForegroundColor Green
Write-Host ""

Write-Host "============================================================"
Write-Host "Starting game processing..."
Write-Host "Each game: Pull image -> Extract files -> Next game"
Write-Host "============================================================"

${gameCommands}

Write-Host ""
Write-Host "############################################################" -ForegroundColor Green
Write-Host " ALL DONE!"
Write-Host " ${gameCount} game(s) processed."
Write-Host " Check ${mountPath} for your games."
Write-Host "############################################################" -ForegroundColor Green
Write-Host ""

`;
            filename = gameCount === 1
                ? `run_${gameIds[0]}.ps1`
                : `run_${gameCount}_games_${timestamp}.ps1`;

        } else {
            // macOS / Linux .sh file
            // Build combined pull+extract commands for each game (single-phase approach)
            const gameCommands = gameIds.map((id, idx) => {
                const game = this.games.find(g => g.id === id);
                const gameName = game ? game.name : id;
                const isLastGame = idx === gameCount - 1;
                return `
echo ""
echo "############################################################"
echo " GAME $((${idx} + 1))/${gameCount}: ${gameName}"
echo "############################################################"
echo "[$(date)] Starting..."
echo ""

# ============================================================
# STEP 1: Pull the Docker image
# ============================================================
echo "[STEP 1/2] Pulling Docker image..."
PULL_SUCCESS=0
RETRY_DELAY=5
for i in 1 2 3 4 5; do
    if [ \$PULL_SUCCESS -eq 0 ]; then
        if ! docker info > /dev/null 2>&1; then
            echo "[WARNING] Docker not responding, attempting recovery..."
            recover_docker
        fi

        if docker pull ${dockerUser}/${repoName}:${id}; then
            PULL_SUCCESS=1
            echo "[OK] Image pulled successfully!"
        else
            echo "[RETRY \$i/5] Pull failed, retrying in \$RETRY_DELAY seconds..."
            sleep \$RETRY_DELAY
            RETRY_DELAY=\$((RETRY_DELAY * 2))
            if [ \$RETRY_DELAY -gt 60 ]; then
                RETRY_DELAY=60
            fi
        fi
    fi
done

if [ \$PULL_SUCCESS -eq 0 ]; then
    echo "[ERROR] Failed to pull ${gameName} after 5 attempts. Skipping..."
    continue 2>/dev/null || true
fi

# ============================================================
# STEP 2: Extract game files to destination
# ============================================================
echo ""
echo "[STEP 2/2] Extracting files to: ${mountPath}/${id}"
echo ""

# Clean up any existing container
docker stop ${id} 2>/dev/null || true
docker rm -f ${id} 2>/dev/null || true

RUN_SUCCESS=0
for attempt in 1 2 3; do
    if [ \$RUN_SUCCESS -eq 0 ]; then
        echo "[ATTEMPT \$attempt/3] Running extraction container..."
        echo ""

        # Run container: mount user's folder to /output, copy game files there
        if docker run -v "${mountPath}:/output" --rm --name ${id} ${dockerUser}/${repoName}:${id} sh -c "echo '=== CONTAINER STARTED ===' && echo 'Copying game files to /output/${id}...' && mkdir -p /output/${id} && cp -rv /home/* /output/${id}/ 2>&1 && echo '' && echo '=== COPY COMPLETE ===' && ls -la /output/${id}/ && echo '' && echo 'Total size:' && du -sh /output/${id}/"; then
            RUN_SUCCESS=1
            echo ""
            echo "============================================================"
            echo "[SUCCESS] ${gameName} extracted successfully!"
            echo "[SAVED TO] ${mountPath}/${id}"
            echo "============================================================"
        else
            echo ""
            echo "[WARNING] Attempt \$attempt/3 failed."
            docker stop ${id} 2>/dev/null || true
            docker rm -f ${id} 2>/dev/null || true
            if [ \$attempt -lt 3 ]; then
                echo "[INFO] Waiting 10 seconds before retry..."
                sleep 10
                recover_docker
            fi
        fi
    fi
done

if [ \$RUN_SUCCESS -eq 0 ]; then
    echo ""
    echo "[ERROR] ${gameName} extraction FAILED after 3 attempts!"
    echo "[ERROR] Check Docker status and try again."
fi`;
            }).join('\n');

            // Determine filename early for instructions
            const scriptFilename = gameCount === 1
                ? `run_${gameIds[0]}.sh`
                : `run_${gameCount}_games.sh`;

            script = `#!/bin/bash
# ============================================================
# Game Library Manager - Docker Runner
# Generated: ${new Date().toISOString()}
# Games: ${gameCount}
# ============================================================
#
# INSTRUCTIONS:
# 1. Make sure Docker is running
# 2. Make this script executable: chmod +x ${scriptFilename}
# 3. Run: ./${scriptFilename}
#
# Each game is fully processed (pull + extract) before moving
# to the next one, so you can see real-time progress.
#
# ============================================================

# ============================================================
# Docker Recovery Function
# Handles Docker daemon issues and connection errors
# ============================================================
recover_docker() {
    echo "[RECOVERY] Attempting Docker recovery..."

    # Detect if running Docker Desktop or native Docker
    if [ -d "/Applications/Docker.app" ] || command -v "Docker" &> /dev/null; then
        # macOS Docker Desktop
        echo "[RECOVERY] Restarting Docker Desktop (macOS)..."
        osascript -e 'quit app "Docker"' 2>/dev/null || true
        sleep 3
        open -a Docker 2>/dev/null || true
        echo "[RECOVERY] Waiting for Docker to initialize..."
        for w in 1 2 3 4 5 6 7 8 9 10 11 12; do
            sleep 5
            if docker info > /dev/null 2>&1; then
                echo "[RECOVERY] Docker is ready!"
                return 0
            fi
            echo "[RECOVERY] Waiting... \$w/12"
        done
    elif command -v systemctl &> /dev/null && systemctl list-unit-files | grep -q docker; then
        # Linux with systemd
        echo "[RECOVERY] Restarting Docker service (Linux)..."
        sudo systemctl restart docker 2>/dev/null || true
        sleep 5
        if docker info > /dev/null 2>&1; then
            echo "[RECOVERY] Docker service restart successful!"
            return 0
        fi
    elif command -v service &> /dev/null; then
        # Linux with init.d
        echo "[RECOVERY] Restarting Docker service..."
        sudo service docker restart 2>/dev/null || true
        sleep 5
        if docker info > /dev/null 2>&1; then
            echo "[RECOVERY] Docker service restart successful!"
            return 0
        fi
    fi

    echo "[RECOVERY] Docker recovery attempted, continuing..."
    return 1
}

echo ""
echo " ===================================="
echo "  Game Library Manager v5.0"
echo "  Processing ${gameCount} game(s)"
echo "  Destination: ${mountPath}"
echo " ===================================="
echo ""

# Initial Docker health check with recovery
echo "Checking Docker status..."
if ! docker info > /dev/null 2>&1; then
    echo "[WARNING] Docker is not responding, attempting to start/restart..."
    recover_docker
    if ! docker info > /dev/null 2>&1; then
        echo "[ERROR] Docker is not running! Please start Docker manually."
        exit 1
    fi
fi

echo "[OK] Docker is running"
echo ""

# Test network connectivity first
echo "Testing network connectivity..."
if ! ping -c 1 registry-1.docker.io > /dev/null 2>&1; then
    echo "[WARNING] Cannot reach Docker Hub, resetting network..."

    # Flush DNS cache
    if command -v systemd-resolve &> /dev/null; then
        sudo systemd-resolve --flush-caches 2>/dev/null || true
    elif command -v resolvectl &> /dev/null; then
        sudo resolvectl flush-caches 2>/dev/null || true
    elif command -v dscacheutil &> /dev/null; then
        sudo dscacheutil -flushcache 2>/dev/null || true
        sudo killall -HUP mDNSResponder 2>/dev/null || true
    fi

    sleep 5

    if ! ping -c 1 registry-1.docker.io > /dev/null 2>&1; then
        echo "[WARNING] Still cannot reach Docker Hub, will retry during processing..."
    fi
fi

echo ""
echo "============================================================"
echo "Starting game processing..."
echo "Each game: Pull image -> Extract files -> Next game"
echo "============================================================"

${gameCommands}

echo ""
echo "############################################################"
echo " ALL DONE!"
echo " ${gameCount} game(s) processed."
echo " Check ${mountPath} for your games."
echo "############################################################"
echo ""
`;
            filename = gameCount === 1
                ? `run_${gameIds[0]}.sh`
                : `run_${gameCount}_games_${timestamp}.sh`;
        }

        // Download the file
        const blob = new Blob([script], { type: 'text/plain' });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = filename;
        a.click();
        URL.revokeObjectURL(url);

        const runHint = this.os === 'windows'
            ? `Run with: .\\${filename}`
            : `Run with: ./${filename}`;
        this.showToast(`Downloaded: ${filename} - ${runHint}`, 'success');
    }

    downloadKillScript(format = 'bat') {
        let script, filename;

        if (this.os === 'windows' && format === 'bat') {
            script = `@echo off
REM Kill all Docker containers (Batch)
echo Stopping and removing all Docker containers...
for /f "tokens=*" %%i in ('docker ps -aq') do docker rm -f %%i 2>nul
echo Done!
`;
            filename = 'kill_all_containers.bat';
        } else if (this.os === 'windows') {
            script = `# Kill all Docker containers (PowerShell)
Write-Host "Stopping and removing all Docker containers..." -ForegroundColor Yellow
docker rm -f $(docker ps -aq) 2>\$null
Write-Host "Done!" -ForegroundColor Green
`;
            filename = 'kill_all_containers.ps1';
        } else {
            script = `#!/bin/bash
# Kill all Docker containers
echo "Stopping and removing all Docker containers..."
docker rm -f $(docker ps -aq) 2>/dev/null
echo "Done!"
`;
            filename = 'kill_all_containers.sh';
        }

        const blob = new Blob([script], { type: 'text/plain' });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = filename;
        a.click();
        URL.revokeObjectURL(url);

        this.showToast(`Downloaded: ${filename}`, 'success');
    }

    toggleRunFormatMenu(e) {
        e.stopPropagation();
        const menu = document.getElementById('runFormatMenu');
        const btn = document.getElementById('runFormatBtn');
        const rect = btn.getBoundingClientRect();

        // Close other menus
        document.getElementById('killFormatMenu').style.display = 'none';

        menu.style.top = `${rect.bottom + 5}px`;
        menu.style.left = `${rect.left}px`;
        menu.style.display = menu.style.display === 'none' ? 'block' : 'none';
    }

    toggleKillFormatMenu(e) {
        e.stopPropagation();
        const menu = document.getElementById('killFormatMenu');
        const btn = document.getElementById('killFormatBtn');
        const rect = btn.getBoundingClientRect();

        // Close other menus
        document.getElementById('runFormatMenu').style.display = 'none';

        menu.style.top = `${rect.bottom + 5}px`;
        menu.style.left = `${rect.left}px`;
        menu.style.display = menu.style.display === 'none' ? 'block' : 'none';
    }

    toggleMoveToMenu(e) {
        e.stopPropagation();
        this.openCategoryMovePanel([...this.selectedGames], 'bulk-toolbar');
    }

    moveSelectedGamesToTab(tabId) {
        this.moveGamesToTab([...this.selectedGames], tabId);
    }

    openCategoryMovePanel(gameIds, mode = 'bulk-toolbar') {
        // CRITICAL: Only admins can move games between categories
        if (!this.isAdmin) {
            this.showToast('Admin access required to move games', 'error');
            return;
        }

        const ids = [...new Set((gameIds || []).filter(Boolean))];
        if (ids.length === 0) {
            this.showToast('Select one or more games first, or use the Move button on a game card', 'warning');
            return;
        }

        this.pendingMoveGameIds = ids;
        this.pendingMoveMode = mode;

        const modal = document.getElementById('moveCategoryModal');
        const search = document.getElementById('moveCategorySearch');
        if (search) search.value = '';
        this.renderCategoryMoveOptions();
        if (modal) {
            modal.classList.add('active');
            modal.setAttribute('aria-hidden', 'false');
        }
        if (search) {
            setTimeout(() => search.focus(), 50);
        }
    }

    closeCategoryMovePanel() {
        const modal = document.getElementById('moveCategoryModal');
        if (modal) {
            modal.classList.remove('active');
            modal.setAttribute('aria-hidden', 'true');
        }
        this.pendingMoveGameIds = [];
        this.pendingMoveMode = null;
    }

    getMovableTabs() {
        const tabsById = new Map(this.tabs.filter(tab => tab.id !== 'all').map(tab => [tab.id, { ...tab }]));
        this.games.forEach(game => {
            if (game.category && game.category !== 'all' && !tabsById.has(game.category)) {
                tabsById.set(game.category, {
                    id: game.category,
                    name: this.formatCategoryLabel(game.category)
                });
            }
        });
        return [...tabsById.values()];
    }

    formatCategoryLabel(categoryId) {
        return String(categoryId || 'Uncategorized')
            .replace(/[_-]+/g, ' ')
            .replace(/\b\w/g, char => char.toUpperCase());
    }

    renderCategoryMoveOptions() {
        const grid = document.getElementById('moveCategoryGrid');
        const summary = document.getElementById('moveCategorySummary');
        const subtitle = document.getElementById('moveCategorySubtitle');
        const search = document.getElementById('moveCategorySearch');
        if (!grid) return;

        const ids = this.pendingMoveGameIds || [];
        const games = ids.map(id => this.games.find(g => g.id === id)).filter(Boolean);
        const query = (search?.value || '').trim().toLowerCase();

        if (summary) {
            if (games.length === 1) {
                summary.textContent = `Moving "${games[0].name}" from ${this.getTabName(games[0].category)}.`;
            } else {
                summary.textContent = `Moving ${games.length} selected games in bulk.`;
            }
        }
        if (subtitle) {
            subtitle.textContent = 'Choose a destination category. The change saves to the live website immediately.';
        }

        const tabs = this.getMovableTabs().filter(tab => {
            const haystack = `${tab.name} ${tab.id}`.toLowerCase();
            return !query || haystack.includes(query);
        });

        if (tabs.length === 0) {
            grid.innerHTML = '<div class="move-category-empty">No matching categories. Create a category from the sidebar first.</div>';
            return;
        }

        const currentCategories = new Set(games.map(game => game.category));
        grid.innerHTML = tabs.map(tab => {
            const isCurrent = games.length === 1 && currentCategories.has(tab.id);
            const isHidden = this.hiddenTabs.has(tab.id);
            const isAdminOnly = this.isTabAdminOnly(tab.id);
            return `
                <button type="button" class="move-category-option ${isCurrent ? 'is-current' : ''}" data-tab="${this.escapeHtml(tab.id)}" role="option" aria-selected="${isCurrent}">
                    <span class="move-category-name">${this.escapeHtml(tab.name)}</span>
                    <span class="move-category-meta">
                        ${this.getTabCount(tab.id)} games
                        ${isCurrent ? ' · current' : ''}
                        ${isHidden ? ' · hidden' : ''}
                        ${isAdminOnly ? ' · admin only' : ''}
                    </span>
                </button>
            `;
        }).join('');

        grid.querySelectorAll('.move-category-option').forEach(option => {
            option.addEventListener('click', () => {
                this.moveGamesToTab(this.pendingMoveGameIds || [], option.dataset.tab);
            });
        });
    }

    getTabName(tabId) {
        const tab = this.tabs.find(t => t.id === tabId);
        return tab ? tab.name : (tabId || 'Uncategorized');
    }

    moveGamesToTab(gameIds, tabId) {
        if (!this.isAdmin) {
            this.showToast('Admin access required to move games', 'error');
            return;
        }

        const ids = [...new Set((gameIds || []).filter(Boolean))];
        if (ids.length === 0) {
            this.showToast('No games selected', 'error');
            return;
        }

        const tab = this.tabs.find(t => t.id === tabId);
        if (!tab || tab.id === 'all') {
            this.showToast('Choose a real category, not All', 'error');
            return;
        }

        const tabName = tab ? tab.name : tabId;
        let movedCount = 0;

        ids.forEach(gameId => {
            const game = this.games.find(g => g.id === gameId);
            if (game && game.category !== tabId) {
                game.category = tabId;
                movedCount++;
            }
        });

        if (movedCount > 0) {
            // Save changes to localStorage for persistence
            this.saveGameChanges();

            // Refresh UI
            this.renderTabs();
            this.filterAndRender();

            this.showToast(`Moved ${movedCount} game(s) to "${tabName}" permanently`, 'success');
        } else {
            this.showToast('Games are already in this category', 'info');
        }

        this.closeCategoryMovePanel();
        ids.forEach(id => this.selectedGames.delete(id));
        this.updateSelectedCount();
    }

    saveGameChanges() {
        // Save modified games to localStorage for immediate UI update
        localStorage.setItem('gameLibraryGames', JSON.stringify(this.games));
        
        // If admin, also save to server to affect all users
        if (this.isAdmin) {
            this.saveAdminConfigToServer();
        }
    }

    loadSavedGameChanges() {
        // Load any saved game modifications from localStorage
        const savedTabs = localStorage.getItem('gameLibraryTabs');
        if (savedTabs) {
            try {
                const normalizedTabs = this.normalizeTabs(JSON.parse(savedTabs));
                if (normalizedTabs.length > 0) {
                    this.tabs = normalizedTabs;
                }
            } catch (e) {
                console.error('Failed to load saved tab changes:', e);
            }
        }

        const savedGames = localStorage.getItem('gameLibraryGames');
        if (savedGames) {
            try {
                const savedGameData = JSON.parse(savedGames);
                // Merge saved changes with loaded games (preserve saved categories)
                savedGameData.forEach(savedGame => {
                    const game = this.games.find(g => g.id === savedGame.id);
                    if (game) {
                        game.category = savedGame.category;
                    }
                });
            } catch (e) {
                console.error('Failed to load saved game changes:', e);
            }
        }
    }

    copyScript() {
        if (!this.currentGame) return;
        const cmd = this.getDockerCommand(this.currentGame.id);
        navigator.clipboard.writeText(cmd).then(() => {
            this.showToast('Docker command copied! Paste in your terminal.', 'success');
        });
    }

    toggleSortMenu(e) {
        const menu = document.getElementById('sortMenu');
        const btn = document.getElementById('sortBtn');
        const rect = btn.getBoundingClientRect();

        menu.style.top = `${rect.bottom + 5}px`;
        menu.style.right = `${window.innerWidth - rect.right}px`;
        menu.style.display = menu.style.display === 'none' ? 'block' : 'none';
    }

    handleSort(sortBy, order) {
        this.sortBy = sortBy;
        this.sortOrder = order;

        document.querySelectorAll('.sort-option').forEach(btn => {
            btn.classList.toggle('active',
                btn.dataset.sort === sortBy && btn.dataset.order === order);
        });

        const indicators = {
            'name-asc': '↓ Name',
            'name-desc': '↑ Name',
            'time-asc': '↓ Time',
            'time-desc': '↑ Time',
            'category-asc': '↓ Cat',
            'size-asc': '↓ Size',
            'size-desc': '↑ Size',
            'date-desc': '↓ Date',  // Newest-Oldest (descending = highest date first)
            'date-asc': '↑ Date',   // Oldest-Newest (ascending = lowest date first)
            'rating-desc': '⭐ Rating',
            'rating-asc': '⭐ Rating↑'
        };
        document.getElementById('sortIndicator').textContent = indicators[`${sortBy}-${order}`] || '↓ Name';
        // Persist sort preference to localStorage
        try { localStorage.setItem('gameLibrarySortPref', JSON.stringify({by: sortBy, order: order})); } catch(e) {}

        document.getElementById('sortMenu').style.display = 'none';
        this.filterAndRender();
    }


    setRating(gameId, rating) {
        // Update in-memory ratings object and persist to localStorage
        if (rating === 0) {
            delete this.ratings[gameId];
        } else {
            this.ratings[gameId] = rating;
        }
        try { localStorage.setItem('gameLibraryRatings', JSON.stringify(this.ratings)); } catch(e) {}
        this.setGameRating(gameId, rating); // also keep individual keys for compatibility
        // Re-render the card's stars in-place
        const card = document.querySelector(`.game-card[data-id="${gameId}"]`);
        if (card) {
            const starsEl = card.querySelector('.rating-stars');
            if (starsEl) {
                const r = this.getGameRating(gameId);
                starsEl.innerHTML = [1,2,3,4,5].map(s => {
                    const filled = s <= r ? 'star-filled' : '';
                    return `<button type="button" class="star-btn ${filled}" data-star="${s}" title="Rate ${s} star${s>1?'s':''}" aria-label="Rate ${gameId} ${s} star${s>1?'s':''}" aria-pressed="${s <= r}">&#9733;</button>`;
                }).join('');
                starsEl.querySelectorAll('.star-btn').forEach(star => {
                    star.addEventListener('click', (e) => {
                        e.stopPropagation();
                        const nr = parseInt(e.target.dataset.star);
                        const cur = this.getGameRating(gameId);
                        this.setRating(gameId, cur === nr ? 0 : nr);
                    });
                });
            }
        }
        if (this.sortBy === 'rating') this.filterAndRender();
    }
    toggleTheme() {
        const currentTheme = document.body.dataset.theme || 'dark';
        const newTheme = currentTheme === 'dark' ? 'light' : 'dark';
        document.body.dataset.theme = newTheme;
        document.getElementById('themeBtn').textContent = newTheme === 'dark' ? '🌙' : '☀️';
        this.settings.theme = newTheme;
        localStorage.setItem('theme', newTheme);
        this.saveSettings();
    }

    openSettings() {
        document.getElementById('gridSize').value = this.settings.gridSize;
        document.getElementById('showTimes').checked = this.settings.showTimes;
        document.getElementById('showCategories').checked = this.settings.showCategories;
        document.getElementById('dockerUsername').value = this.settings.dockerUsername;
        document.getElementById('repoName').value = this.settings.repoName;
        document.getElementById('mountPath').value = this.settings.mountPath;
        document.getElementById('settingsModal').classList.add('active');
    }

    loadSettings() {
        const defaults = {
            theme: 'dark',
            gridSize: 'medium',
            showTimes: true,
            showCategories: true,
            dockerUsername: 'michadockermisha',
            repoName: 'backup',
            mountPath: 'E:/games'
        };

        try {
            const saved = localStorage.getItem('gameLibrarySettings');
            const settings = saved ? { ...defaults, ...JSON.parse(saved) } : defaults;
            // 'theme' standalone key takes priority for persistence compatibility
            const standaloneTheme = localStorage.getItem('theme');
            if (standaloneTheme === 'light' || standaloneTheme === 'dark') {
                settings.theme = standaloneTheme;
            }

            // Sync global mount path input
            setTimeout(() => {
                const globalPath = document.getElementById('globalMountPath');
                if (globalPath) {
                    globalPath.value = settings.mountPath;
                }
            }, 100);

            return settings;
        } catch {
            return defaults;
        }
    }

    saveSettings() {
        localStorage.setItem('gameLibrarySettings', JSON.stringify(this.settings));
    }

    loadInstalledGames() {
        try {
            const saved = localStorage.getItem('installedGames');
            if (saved) {
                this.manualInstalledGames = new Set(JSON.parse(saved));
            }
            this.installedGames = new Set(this.manualInstalledGames);
        } catch {
            this.manualInstalledGames = new Set();
            this.installedGames = new Set();
        }
    }

    saveInstalledGames() {
        localStorage.setItem('installedGames', JSON.stringify([...this.manualInstalledGames]));
    }

    getInstalledScannerUrl() {
        const localHost = ['localhost', '127.0.0.1', '::1'].includes(window.location.hostname);
        return localHost ? '/api/installed-games' : 'http://127.0.0.1:3000/api/installed-games';
    }

    startInstalledScanner() {
        if (this.installedScanInterval) clearInterval(this.installedScanInterval);
        this.scanInstalledGames();
        this.installedScanInterval = setInterval(() => this.scanInstalledGames(), this.installedScanIntervalMs);
        this.renderInstalledScanStatus();
    }

    async scanInstalledGames() {
        if (this.installedScanInFlight) return;
        this.installedScanInFlight = true;
        this.installedScannerState = 'scanning';
        this.renderInstalledScanStatus();
        const controller = new AbortController();
        const timeout = setTimeout(() => controller.abort(), 120000);

        try {
            const response = await fetch(`${this.getInstalledScannerUrl()}?_=${Date.now()}`, {
                headers: { Accept: 'application/json', 'Cache-Control': 'no-cache' },
                cache: 'no-store',
                signal: controller.signal
            });
            if (!response.ok) throw new Error(`local scanner HTTP ${response.status}`);
            const data = await response.json();
            if (!data.success || !Array.isArray(data.games)) throw new Error(data.error || 'invalid scanner response');

            const entries = data.games
                .filter(entry => entry && entry.path && entry.name)
                .map(entry => ({
                    name: String(entry.name),
                    path: String(entry.path),
                    root: String(entry.root || ''),
                    executable: entry.executable ? String(entry.executable) : null,
                    marker: entry.marker ? String(entry.marker) : null
                }));
            const matchedIds = new Set();
            const scanMatches = new Map();
            entries.forEach(entry => {
                const match = this.findGameForInstalledEntry(entry);
                if (match) matchedIds.add(match.id);
                scanMatches.set(entry.path, match ? match.id : null);
            });

            const signature = JSON.stringify(entries.map(entry => [entry.name, entry.path, entry.executable, entry.marker]));
            const changed = signature !== this.installedScanSignature;
            this.installedScanSignature = signature;
            this.installedScanEntries = entries;
            this.installedScanMatches = scanMatches;
            this.scannedInstalledGames = matchedIds;
            // A successful machine scan is authoritative; localStorage remains a fallback only.
            this.installedGames = new Set(matchedIds);
            this.installedScannerState = 'connected';
            this.installedScanRoots = Array.isArray(data.roots) ? data.roots : [];
            this.installedScanLastUpdated = data.scannedAt || new Date().toISOString();

            if (changed) {
                this.renderTabs();
                this.filterAndRender();
                this.updateStatsDashboard();
            }
        } catch (error) {
            // Keep the last successful scan visible during a transient local-server failure.
            if (this.scannedInstalledGames === null) this.installedScannerState = 'unavailable';
            else this.installedScannerState = 'stale';
            this.installedScanError = error.name === 'AbortError' ? 'scan timed out' : error.message;
        } finally {
            clearTimeout(timeout);
            this.installedScanInFlight = false;
            this.renderInstalledScanStatus();
        }
    }

    findGameForInstalledEntry(entry) {
        const entryKeys = [entry.name, entry.path, entry.executable]
            .filter(Boolean)
            .map(value => this.normalizeLookupKey(value));
        if (entryKeys.length === 0) return null;

        const exact = this.games.find(game => {
            const gameKeys = [game.id, game.name].map(value => this.normalizeLookupKey(value));
            return gameKeys.some(key => key && entryKeys.includes(key));
        });
        if (exact) return exact;

        // Use conservative containment only for meaningful names to avoid false positives.
        let best = null;
        let bestLength = 0;
        this.games.forEach(game => {
            const gameKeys = [game.id, game.name].map(value => this.normalizeLookupKey(value)).filter(key => key.length >= 6);
            gameKeys.forEach(gameKey => entryKeys.forEach(entryKey => {
                if ((entryKey.includes(gameKey) || gameKey.includes(entryKey)) && gameKey.length > bestLength) {
                    best = game;
                    bestLength = gameKey.length;
                }
            }));
        });
        return best;
    }

    getInstalledDisplayGames() {
        const matched = this.games.filter(game => this.installedGames.has(game.id));
        const matchedIds = new Set(matched.map(game => game.id));
        const localOnly = this.scannedInstalledGames === null ? [] : this.installedScanEntries
            .filter(entry => !this.installedScanMatches.get(entry.path))
            .map(entry => ({
                id: `local-installed-${this.normalizeLookupKey(entry.path || entry.name)}`,
                name: entry.name,
                category: 'installed',
                image: this.createGeneratedCoverDataUrl(entry.name),
                localPath: entry.path,
                executable: entry.executable,
                isLocalInstalled: true
            }));
        return [...matched, ...localOnly.filter(game => !matchedIds.has(game.id))];
    }

    renderInstalledScanStatus() {
        const status = document.getElementById('installedScanStatus');
        if (!status) return;
        const count = this.installedScanEntries.length;
        const time = this.installedScanLastUpdated ? new Date(this.installedScanLastUpdated).toLocaleTimeString() : null;
        status.className = `installed-scan-status ${this.installedScannerState}`;
        if (this.installedScannerState === 'connected') {
            status.textContent = `🔴 Installed scanner connected • ${count} found • updated ${time || 'now'}`;
        } else if (this.installedScannerState === 'scanning') {
            status.textContent = count > 0 ? `🔄 Installed scanner refreshing • ${count} found` : '🔄 Scanning C:\\Games, F:\\Games, and E:\\Games...';
        } else if (this.installedScannerState === 'stale') {
            status.textContent = `⚠️ Scanner temporarily unavailable • showing last scan (${count} found)`;
        } else {
            status.textContent = '⚠️ Local scanner not connected • run “node server.js” on this machine to scan the three game folders';
        }
    }

    // Admin authentication using SHA-256 hash comparison
    async attemptAdminLogin(password) {
        // Hash the input password and compare to stored hash
        // This prevents plaintext password exposure in source code
        const inputHash = await this.hashPassword(password);

        if (inputHash === this.adminHash) {
            this.isAdmin = true;
            document.body.classList.add('is-admin');
            document.getElementById('adminLoginBox').style.display = 'none';
            document.getElementById('adminLoggedBox').style.display = 'flex';

            // Show admin-only features
            const moveToContainer = document.querySelector('.move-to-container');
            if (moveToContainer) {
                moveToContainer.style.display = 'block';
            }

            // Load latest config from server (not localStorage) to get all saved categories
            await this.loadAdminConfigFromServer();
            this.renderTabs();
            this.filterAndRender(); // Re-render to show admin features
            this.updateSelectedCount(); // Update UI to show admin features
            this.showToast('👑 Admin access granted!', 'success');
        } else {
            this.showToast('❌ Invalid password', 'error');
        }
    }

    // SHA-256 hash function using Web Crypto API
    async hashPassword(password) {
        const encoder = new TextEncoder();
        const data = encoder.encode(password);
        const hashBuffer = await crypto.subtle.digest('SHA-256', data);
        const hashArray = Array.from(new Uint8Array(hashBuffer));
        return hashArray.map(b => b.toString(16).padStart(2, '0')).join('');
    }

    adminLogout() {
        this.isAdmin = false;
        document.body.classList.remove('is-admin');
        document.getElementById('adminLoginBox').style.display = 'flex';
        document.getElementById('adminLoggedBox').style.display = 'none';
        
        // Hide admin-only features
        const moveToContainer = document.querySelector('.move-to-container');
        if (moveToContainer) {
            moveToContainer.style.display = 'none';
        }
        
        this.renderTabs();
        this.filterAndRender();
        this.updateSelectedCount(); // Update UI to hide admin features
        this.showToast('Logged out', 'info');
    }

    // Ensures non-admin state on page load - CRITICAL for security
    ensureNonAdminState() {
        this.isAdmin = false;
        document.body.classList.remove('is-admin');
        const loginBox = document.getElementById('adminLoginBox');
        const loggedBox = document.getElementById('adminLoggedBox');
        if (loginBox) loginBox.style.display = 'flex';
        if (loggedBox) loggedBox.style.display = 'none';
        
        // CRITICAL: Hide all admin-only features on page load
        const moveToContainer = document.querySelector('.move-to-container');
        if (moveToContainer) {
            moveToContainer.style.display = 'none';
        }
    }

    // Load admin configuration from server - affects ALL users immediately
    async loadAdminConfigFromServer() {
        try {
            // CRITICAL: Cache-busting to ALWAYS get latest changes
            const cacheBuster = `?t=${Date.now()}&v=${Math.random()}&nocache=1`;
            const response = await fetch(`/api/admin-config${cacheBuster}`, {
                method: 'GET',
                headers: {
                    'Content-Type': 'application/json',
                    'Cache-Control': 'no-cache, no-store, must-revalidate',
                    'Pragma': 'no-cache',
                    'Expires': '0'
                }
            });

            if (response.ok) {
                const data = await response.json();
                if (data.success && data.config) {
                    // Apply server admin rules to ALL users
                    this.hiddenTabs = new Set(data.config.hiddenTabs || []);

                    if (Array.isArray(data.config.tabs) && data.config.tabs.length > 0) {
                        this.tabs = this.normalizeTabs(data.config.tabs);
                        localStorage.setItem('gameLibraryTabs', JSON.stringify(this.tabs));
                    }

                    // Store server categories for reference
                    this.serverGameCategories = data.config.gameCategories || {};

                    // Apply game category overrides from server (highest priority - overrides localStorage)
                    const serverCategories = data.config.gameCategories || {};
                    const categoryCount = Object.keys(serverCategories).length;
                    if (categoryCount > 0 && this.games.length > 0) {
                        Object.entries(serverCategories).forEach(([gameId, category]) => {
                            const game = this.games.find(g => g.id === gameId);
                            if (game) {
                                game.category = category;
                            }
                        });
                        console.log(`📡 Applied ${categoryCount} game category overrides from server`);
                    }

                    // Sync server state to localStorage as backup
                    if (categoryCount > 0) {
                        localStorage.setItem('gameLibraryGames', JSON.stringify(this.games));
                    }

                    // Track config version for polling
                    if (data.configVersion) {
                        this._lastConfigVersion = data.configVersion;
                    }
                    this._lastAdminConfigSignature = this.getAdminConfigSignature(data.config);
                    console.log('Loaded admin config from server:', data.config);
                }
            } else {
                // API not available (Vercel), load from static file
                console.log('API not available, loading from static file (Vercel mode)');
                await this.loadStaticAdminConfig();
            }
        } catch (error) {
            console.error('Failed to load admin config from server:', error);
            // Network error, try static file
            await this.loadStaticAdminConfig();
        }
    }

    // Load admin config from static JSON file (for Vercel deployment)
    async loadStaticAdminConfig() {
        try {
            const staticResponse = await fetch('/data/admin-config.json?t=' + Date.now());
            if (staticResponse.ok) {
                const staticConfig = await staticResponse.json();
                console.log('Loaded admin config from static file (Vercel fallback)');
                
                // Apply the static config
                this.adminConfig = staticConfig;
                this._lastAdminConfigSignature = this.getAdminConfigSignature(staticConfig);
                
                // Set hidden tabs
                if (staticConfig.hiddenTabs && Array.isArray(staticConfig.hiddenTabs)) {
                    this.hiddenTabs = new Set(staticConfig.hiddenTabs);
                }

                if (Array.isArray(staticConfig.tabs) && staticConfig.tabs.length > 0) {
                    this.tabs = this.normalizeTabs(staticConfig.tabs);
                    localStorage.setItem('gameLibraryTabs', JSON.stringify(this.tabs));
                }
                
                // Apply game category overrides
                if (staticConfig.gameCategories && this.games && this.games.length > 0) {
                    for (const [gameId, category] of Object.entries(staticConfig.gameCategories)) {
                        const game = this.games.find(g => g.id === gameId);
                        if (game) {
                            game.originalCategory = game.originalCategory || game.category;
                            game.category = category;
                        }
                    }
                    console.log(`📡 Applied ${Object.keys(staticConfig.gameCategories).length} game category overrides from static file`);
                }
                
                console.log('Applied static config - hidden tabs:', Array.from(this.hiddenTabs));
            } else {
                // Final fallback to localStorage
                this.loadHiddenTabs();
            }
        } catch (staticError) {
            console.error('Failed to load static admin config:', staticError);
            // Final fallback to localStorage
            this.loadHiddenTabs();
        }
    }

    // Save admin configuration to server - immediately affects ALL users
    async saveAdminConfigToServer() {
        if (!this.isAdmin) {
            console.error('Only admins can save configuration');
            return;
        }

        // Serialize rapid edits. A newer edit marks the current request dirty and
        // is sent immediately after it completes, so no admin change is lost.
        if (this._adminSaveInFlight) {
            this._adminSaveDirty = true;
            return this._adminSaveInFlight;
        }

        const saveOperation = (async () => {
            let lastError = null;
            for (let attempt = 0; attempt < 4; attempt++) {
                try {
                    // Collect ALL game category changes (every single game)
                    const gameCategories = {};
                    this.games.forEach(game => {
                        if (game.category) gameCategories[game.id] = game.category;
                    });

                    const payload = {
                        hiddenTabs: [...this.hiddenTabs],
                        gameCategories,
                        tabs: this.normalizeTabs(this.tabs)
                    };

                    const response = await fetch('/api/admin-config', {
                        method: 'POST',
                        headers: {
                            'Content-Type': 'application/json',
                            'X-Admin-Token': 'glm-admin-2024'
                        },
                        body: JSON.stringify(payload)
                    });
                    if (!response.ok) throw new Error('Server returned ' + response.status);

                    const data = await response.json();
                    this._lastConfigVersion = data.configVersion || this._lastConfigVersion;
                    this._lastAdminConfigSignature = this.getAdminConfigSignature(payload);
                    console.log('Admin config saved to server:', data);
                    this.showToast('Changes saved permanently to server!', 'success');
                    return data;
                } catch (error) {
                    lastError = error;
                    console.error('Failed to save admin config to server:', error);
                    if (attempt < 3) {
                        const delay = (attempt + 1) * 2000;
                        console.log(`Retrying save in ${delay}ms (attempt ${attempt + 2}/4)...`);
                        await new Promise(resolve => setTimeout(resolve, delay));
                    }
                }
            }
            this.showToast('Failed to save to server after 4 attempts', 'error');
            return { success: false, error: lastError };
        })();

        this._adminSaveInFlight = saveOperation.finally(() => {
            const saveAgain = this._adminSaveDirty;
            this._adminSaveDirty = false;
            this._adminSaveInFlight = null;
            if (saveAgain) this.saveAdminConfigToServer();
        });
        return this._adminSaveInFlight;
    }

    // Legacy localStorage methods (kept for fallback)
    loadHiddenTabs() {
        try {
            const saved = localStorage.getItem('hiddenTabs');
            if (saved) {
                this.hiddenTabs = new Set(JSON.parse(saved));
            }
        } catch {
            this.hiddenTabs = new Set();
        }
    }

    saveHiddenTabs() {
        // Save to localStorage for immediate UI update
        localStorage.setItem('hiddenTabs', JSON.stringify([...this.hiddenTabs]));
        // Then save to server to affect all users
        this.saveAdminConfigToServer();
    }

    toggleTabVisibility(tabId) {
        if (!this.isAdmin) return;

        const wasHidden = this.hiddenTabs.has(tabId);
        
        if (wasHidden) {
            this.hiddenTabs.delete(tabId);
            this.showToast(`✅ Tab "${tabId}" unlocked - games now visible in "all" tab`, 'success');
        } else {
            this.hiddenTabs.add(tabId);
            this.showToast(`🔒 Tab "${tabId}" hidden - games removed from "all" tab`, 'warning');
        }
        
        // CRITICAL: Save to server AND immediately update UI
        this.saveHiddenTabs(); // Saves to server, affects ALL users
        this.renderTabs();     // Re-render tabs with updated visibility icons
        this.filterAndRender(); // CRITICAL: Re-render games to immediately hide/show in "all" tab
        
        console.log(`Tab "${tabId}" ${wasHidden ? 'unlocked' : 'hidden'} - changes applied instantly`);
    }

    getGameRating(gameId) {
        return parseInt(localStorage.getItem('gameRating_' + gameId) || '0', 10);
    }

    setGameRating(gameId, rating) {
        if (rating === 0) {
            localStorage.removeItem('gameRating_' + gameId);
        } else {
            localStorage.setItem('gameRating_' + gameId, String(rating));
        }
    }

    toggleInstalled(gameId) {
        if (this.manualInstalledGames.has(gameId)) {
            this.manualInstalledGames.delete(gameId);
            this.showToast(`${gameId} marked as not installed`, 'info');
        } else {
            this.manualInstalledGames.add(gameId);
            this.showToast(`${gameId} marked as installed`, 'success');
        }
        if (this.scannedInstalledGames === null) {
            this.installedGames = new Set(this.manualInstalledGames);
        }
        this.saveInstalledGames();
        this.filterAndRender();
        this.updateStatsDashboard();
    }

    toggleWishlist(gameId) {
        if (this.wishlist.has(gameId)) {
            this.wishlist.delete(gameId);
            this.showToast('Removed from wishlist', 'info');
        } else {
            this.wishlist.add(gameId);
            this.showToast('Added to wishlist ♥', 'success');
        }
        localStorage.setItem('gameWishlist', JSON.stringify([...this.wishlist]));
        this.renderTabs();
        this.filterAndRender();
    }

    toggleInstalledFilter() {
        this.showInstalledOnly = !this.showInstalledOnly;
        const btn = document.getElementById('showInstalledBtn');
        if (this.showInstalledOnly) {
            btn.textContent = '📋 Show All';
            btn.classList.add('active');
            this.showToast(`Showing ${this.installedGames.size} installed games`, 'info');
        } else {
            btn.textContent = '✅ Show Installed';
            btn.classList.remove('active');
            this.showToast('Showing all games', 'info');
        }
        this.filterAndRender();
    }

    applySettings() {
        document.body.dataset.theme = this.settings.theme;
        document.body.dataset.grid = this.settings.gridSize;
        document.getElementById('themeBtn').textContent = this.settings.theme === 'dark' ? '🌙' : '☀️';

        // Restore sort indicator from persisted preference
        const _sortIndEl = document.getElementById('sortIndicator');
        if (_sortIndEl) {
            const _ind = {'name-asc':'\u2193 Name','name-desc':'\u2191 Name','time-asc':'\u2193 Time','time-desc':'\u2191 Time','category-asc':'\u2193 Cat','size-asc':'\u2193 Size','size-desc':'\u2191 Size','date-desc':'\u2193 Date','date-asc':'\u2191 Date','rating-desc':'\u2B50 Rating','rating-asc':'\u2B50 Rating\u2191'};
            _sortIndEl.textContent = _ind[this.sortBy + '-' + this.sortOrder] || '\u2193 Name';
            document.querySelectorAll('.sort-option').forEach(function(btn) {
                btn.classList.toggle('active', btn.dataset.sort === this.sortBy && btn.dataset.order === this.sortOrder);
            }.bind(this));
        }
    }

    exportData() {
        const data = {
            settings: this.settings,
            selectedGames: [...this.selectedGames],
            exportDate: new Date().toISOString()
        };
        const blob = new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = `game-library-settings-${Date.now()}.json`;
        a.click();
        URL.revokeObjectURL(url);
        this.showToast('Settings exported!', 'success');
    }

    importData() {
        const input = document.createElement('input');
        input.type = 'file';
        input.accept = '.json';
        input.onchange = (e) => {
            const file = e.target.files[0];
            if (!file) return;

            const reader = new FileReader();
            reader.onload = (e) => {
                try {
                    const data = JSON.parse(e.target.result);
                    if (data.settings) {
                        this.settings = { ...this.settings, ...data.settings };
                        this.saveSettings();
                        this.applySettings();
                    }
                    if (data.selectedGames) {
                        this.selectedGames = new Set(data.selectedGames);
                        this.updateSelectedCount();
                    }
                    this.filterAndRender();
                    this.showToast('Settings imported!', 'success');
                } catch {
                    this.showToast('Invalid file format', 'error');
                }
            };
            reader.readAsText(file);
        };
        input.click();
    }

    showLoading(show) {
        document.getElementById('loadingIndicator').style.display = show ? 'flex' : 'none';
        document.getElementById('gamesGrid').style.display = show ? 'none' : 'grid';
    }

    showToast(message, type = 'info') {
        const container = document.getElementById('toastContainer');
        const toast = document.createElement('div');
        toast.className = `toast ${type}`;

        const icons = {
            success: '✅',
            error: '❌',
            warning: '⚠️',
            info: 'ℹ️'
        };

        toast.innerHTML = `
            <span>${icons[type] || icons.info}</span>
            <span>${message}</span>
        `;
        container.appendChild(toast);

        // Auto-dismiss with smooth exit animation
        setTimeout(() => {
            toast.style.animation = 'toastSlideIn 0.3s ease reverse forwards';
            setTimeout(() => toast.remove(), 300);
        }, 3000);
    }

    // ============================================
    // SCROLL EFFECTS
    // ============================================
    setupScrollEffects() {
        const header = document.querySelector('.header');
        const scrollTopBtn = document.getElementById('scrollTopBtn');
        const content = document.querySelector('.content');

        let ticking = false;

        const handleScroll = () => {
            if (!ticking) {
                window.requestAnimationFrame(() => {
                    const scrollY = window.scrollY;

                    // Header shadow on scroll
                    if (scrollY > 10) {
                        header.classList.add('scrolled');
                    } else {
                        header.classList.remove('scrolled');
                    }

                    // Scroll to top button visibility
                    if (scrollY > 300) {
                        scrollTopBtn.classList.add('visible');
                    } else {
                        scrollTopBtn.classList.remove('visible');
                    }

                    ticking = false;
                });
                ticking = true;
            }
        };

        window.addEventListener('scroll', handleScroll, { passive: true });

        // Scroll to top click handler
        scrollTopBtn.addEventListener('click', () => {
            window.scrollTo({
                top: 0,
                behavior: 'smooth'
            });
        });
    }

    // ============================================
    // MOBILE SIDEBAR
    // ============================================
    setupMobileSidebar() {
        const sidebar = document.querySelector('.sidebar');
        const sidebarToggle = document.getElementById('sidebarToggle');
        const sidebarOverlay = document.getElementById('sidebarOverlay');

        const openSidebar = () => {
            sidebar.classList.add('open');
            sidebarOverlay.classList.add('active');
            sidebarToggle.innerHTML = '✕';
            document.body.style.overflow = 'hidden';
        };

        const closeSidebar = () => {
            sidebar.classList.remove('open');
            sidebarOverlay.classList.remove('active');
            sidebarToggle.innerHTML = '☰';
            document.body.style.overflow = '';
        };

        sidebarToggle.addEventListener('click', () => {
            if (sidebar.classList.contains('open')) {
                closeSidebar();
            } else {
                openSidebar();
            }
        });

        sidebarOverlay.addEventListener('click', closeSidebar);

        // Close sidebar when a tab is clicked (mobile)
        document.getElementById('tabsContainer').addEventListener('click', (e) => {
            if (e.target.closest('.tab-btn') && window.innerWidth <= 1024) {
                closeSidebar();
            }
        });

        // Close sidebar on window resize if becoming desktop
        window.addEventListener('resize', () => {
            if (window.innerWidth > 1024 && sidebar.classList.contains('open')) {
                closeSidebar();
            }
        });
    }

    // ============================================
    // KEYBOARD HINTS
    // ============================================
    setupKeyboardHints() {
        const keyboardHints = document.getElementById('keyboardHints');
        let hideTimeout;

        // Show hints briefly on page load
        setTimeout(() => {
            keyboardHints.classList.add('visible');
            hideTimeout = setTimeout(() => {
                keyboardHints.classList.remove('visible');
            }, 5000);
        }, 2000);

        // Show hints when user starts typing
        document.addEventListener('keydown', (e) => {
            // Don't show if already in an input
            if (e.target.tagName === 'INPUT' || e.target.tagName === 'TEXTAREA') {
                return;
            }

            clearTimeout(hideTimeout);
            keyboardHints.classList.add('visible');

            hideTimeout = setTimeout(() => {
                keyboardHints.classList.remove('visible');
            }, 3000);
        });
    }

    // ============================================
    // ENHANCED IMAGE LOADING
    // ============================================

    // Known Steam App ID mappings for reliable cover fetching
    getKnownSteamAppIds() {
        return {
            'doomthedarkages': '3017860',
            'silenceofthesiren': '2147380',
            'stillwakesthedeep': '1622910',
            'scorn': '698670',
            'hereticsfork': '2181610',
            'hereticsforkthegame': '2181610',
            'fortsolis': '1931730',
            'thekingiswatching': '2753900',
            'deadlinedelivery': '3745310',
            'enshrouded': '1203620',
            'sengokudynasty': '1702010',
            'flintlocksiegedownleft': '1832040',
            'starshiptroopers': '1202130',
            'starshiptroopersultimate': '1202130',
            'starshiptroopersextermination': '1268750',
            'starshiptroopers extermination': '1268750',
            'starshiptroopersterrancommand': '1202130',
            'grindsurvivors': '3816930',
            'formulalegends': '3194360',
            'kunitsugami': '2510710',
            'kunitsugamipathofthegoddess': '2510710',
            'ashrust': '1358490',
            'ashandrust': '1358490',
            'cornershopnightshift': '3306990',
            'cornershop': '3306990',
            'caribbeanlegendageofpirates': '3549020',
            'caribbeanlegendagendag': '3549020',
            'caribbeanlegenddaggersoffate': '3549020',
            'caribbeanlegend': '2230980',
            'dicewithdeath': '3435260',
            'sculplings': '3062680',
            'sculptings': '3062680',
            'dragonkinthebanished': '1863430',
            'dragonkinthebanish': '1863430',
            'theartisanofgilmith': '4160210',
            'theartisanofglimmith': '4160210',
            'theratline': '3756940',
            'royalrevoltsurvivors': '2960490',
            'tombbraiderililiremastered': '2478970',
            'tombraiderililiremastered': '2478970',
            'tombraider13remastered': '2478970',
            'tombraiderremastered': '2478970',
            'kaijucrackingcorporation': '3586410',
            'kaijucrackingcorpo': '3586410',
            'magicraft': '2103140',
            'mirrorsedgecatalyst': '1233570',
            'mirrorsedge': '1233570',
            'ultratron': '219190',
            'jackal': '1259580',
            'myheroaccademia': '2753010',
            'myheroacademiaallinone': '2753010',
            'romeoanddeadman': '2799610',
            'romeoisadeadman': '2799610',
            'stygbladesofgreed': '2870970',
            'styxbladesofgreed': '2870970',
            'styxbladesofgreedpc': '2870970',
            'styxshardsoffdarkenss': '2037860',
            'demontides': '2594340',
            'mailchildoffagesstor': '2934800',
            'mailchildofages': '2934800',
            'gravity': '1598750',
            'talesoflittlemen': '2839830',
            'superpixelmergeballs': '2798250',
            'monsterworld': '2929510',
            'thethaumaturge': '2231110',
            'endlingextinctionisforever': '1356640',
            'enshrouded': '1203620',
            'eternalstrands': '1922560',
            'expedition33': '2753390',
            'flintlocksiegedownleft': '1832040',
            'schedulei': '3164500',
            'wanderstop': '2131080',
            'southofmidnight': '2454920',
            'themidnightwalk': '2498690',
            'inzoi': '2456740',
            'dawnoftheashenqueen': '2849700',
            'nobodywantstodie': '2148970',
            'avowed': '1545810',
            'kingdomcomedeliverance2': '1771300',
            'metaphorredfantazio': '2679460',
            'metaphorreFantazio': '2679460',
            'sensuassagahellblade2': '2740960',
            'sensuassagahellbladeii': '2740960',
            'stalker2': '1643320',
            'eldenringnightreign': '2622380',
            'splitfiction': '2366570',
            'atomfall': '2722040',
            'clair obscur expedition 33': '2753390',
            'oblivionremastered': '2623190',
            'sifu': '2138710',
            'balatro': '2379780',
            'neckbreak': '2628920',
            'tinyglade': '2198150',
            'squeakwithagun': '2067050',
            'squirrelwithagun': '2067050',
            'daysomething': '2332620',
            'goatsimulator3': '1113750',
            'hellpoint': '628670',
            'lethalleagueblaze': '553310',
            'wobblylife': '1211020',
            'wreckfest': '228380',
            'paintthetown': '337320',
            'paintthetownred': '337320',
            'roboquest': '692890',
            'yakuza0': '638970',
            'cuphead': '268910',
            'cyberpunk2077': '1091500',
            'deathstranding': '1190460',
            'darksouls3': '374320',
            'darksoulsremastered': '570940',
            'darksouls2': '335300',
            'demonsouls': '2816700',
            'doometernal': '782330',
            'dysmantle': '846770',
            'generationzero': '704270',
            'grounded': '962130',
            'gunfirereborn': '1217060',
            'humansfallfalt': '477160',
            'humanfallflat': '477160',
            'mortalkombat1': '1971870',
            'mortalshell': '1110910',
            'nioh': '485510',
            'nioh2': '1325200',
            'monsterrise': '1446780',
            'monsterhunterrise': '1446780',
            'steinsgate': '412830',
            'steinsgateelite': '819030',
            'stickfightthegame': '674940',
            'spellforce3': '311290',
            'spellforce3reforced': '311290',
            'pathfinderwrath': '1184370',
            'pathfinderwrathoftherighteous': '1184370',
            'yakuzalikeadragon': '1235140',
            'yakuza5': '1105510',
            'yakuza6': '1388590',
            'yakuzakiwami': '834530',
            'yakuzakiwami2': '927380',
            'wolongfallendynasty': '1448440',
            'talosprinciple': '257510',
            'talesofArise': '740130',
            'talesrise': '740130',
            'overcooked2': '728880',
            'outward': '794260',
            'voidbastards': '857980',
            'speedrunners': '207140',
            'gangbeasts': '467030',
            'flatout': '6220',
            'flatout2': '2990',
            'magicka': '42910',
            'magicka2': '238370',
            'unrailed': '1016920',
            'danganronpa': '413410',
            'indianajones': '2677660',
            'neopets': '2671670',
            'legobatman2': '130100',
            'legobatman3': '313390',
            'legobatman3beyondgotham': '313390',
            'legocityundercover': '578330',
            'legoharrypotter57': '204120',
            'legoindianajones': '32330',
            'legojurassicworld': '352400',
            'legomarvel': '249130',
            'legoninjago': '640590',
            'legostarwars': '32440',
            'legostarwarsthecomplersaga': '32440',
            'legoworlds': '332310',
            'transportfever2': '1066780',
            'handofgod': '2655800',
            'hypercharge': '523660',
            'melatonin': '1585220',
            'bully': '12200',
            'chernobylite': '1016800',
            'critstales': '1071800',
            'duckgame': '312530',
            'enotria': '2102450',
            'enotriathelastsong': '2102450',
            'enterthegungeon': '311690',
            'enterthegungeonadvanced': '311690',
            'eastshade': '715560',
            'frogun': '1460340',
            'fullfuries': '2590',
            'fullmetalfuries': '416600',
            'gardenpaws': '840010',
            'gatoroboto': '916730',
            'griftlands': '601840',
            'goingunder': '1154810',
            'greakmemoriesofazur': '1311070',
            'hammerwatchii': '2742630',
            'heaveho': '905340',
            'immortalsofaveum': '1736810',
            'indivisible': '421170',
            'journeytothesavageplanet': '530120',
            'jurassicworldevolution2': '1244460',
            'kaothekangaroo': '1370280',
            'keeptalkingandnobodyexplodes': '341800',
            'killerinstinct': '577940',
            'kingdomcomedeliverance': '379430',
            'kingsbounty2': '1135510',
            'kona2': '1229560',
            'lethalleagueblaze': '553310',
            'lifeisstrangedoubleexposure': '2464190',
            'littlewood': '894940',
            'lollipopchainsaw': '2546300',
            'lollipopchainawrepop': '2546300',
            'loopmancer': '1580040',
            'luigismansion2hd': '2710170',
            'luigismansion3': '2710180',
            'mafiadefinitiveedition': '1030840',
            'marioluigibrothership': '2710160',
            'mariovsdondkonkeykong': '2710190',
            'metalgearsolid2': '2740960',
            'metalgearsolid3': '945950',
            'metalhellsinger': '1061910',
            'middleearthshadowofwar': '356190',
            'mindseye': '2747040',
            'minecraftdungeons': '1672970',
            'minuteofislands': '1007750',
            'movingout2': '1626480',
            'myfriendlyneighborhood': '1574300',
            'mysummercar': '516750',
            'mytimeatsandrock': '1084600',
            'ninjagaidenmastercollection': '1580790',
            'norco': '1221250',
            'neocab': '794540',
            'nexmachina': '404540',
            'nidhogg2': '535520',
            'nightcall': '680030',
            'nineparchments': '471550',
            'nostraightroads': '1306410',
            'obduction': '306760',
            'observation': '906100',
            'octogeddon': '525620',
            'omensight': '455820',
            'onirism': '703310',
            'openroads': '1497780',
            'outcast': '618970',
            'outcastanewbeginning': '618970',
            'phogs': '850320',
            'phantomfury': '1593870',
            'princeofpersia': '2436640',
            'princeofpersiathesandsoftime': '2436640',
            'rajiancientepic': '730390',
            'redfactionguerrilla': '667720',
            'resistance3': '2741160',
            'rivercitygirls2': '1974430',
            'rivercitygirlszero': '1530760',
            'road96': '1466640',
            'samuraigunn2': '1397790',
            'scarlethollow': '1419560',
            'screencheat': '301970',
            'selfloss': '1399100',
            'shadowofthecolossus': '2690510',
            'sixdaysinfallujah': '1548850',
            'skullgirls': '245170',
            'skullgirls2ndenchore': '245170',
            'somerville': '1661350',
            'steep': '460920',
            'strayblade': '1492140',
            'sunlesssea': '304650',
            'sunlessskies': '596970',
            'superbombermanr': '702700',
            'superbombermanr2': '1736020',
            'syberia': '1410710',
            'syberiathrworldbefore': '1410710',
            'tdpaahouseofashes': '1281590',
            'tdpathedevilinme': '1567020',
            'thedarkpicturesanthology': '1281590',
            'thedarknesslittlehope': '1194630',
            'thedarkpicturesanthologylittlehope': '1194630',
            'thedarkpicturesanthologymanofmedan': '939850',
            'tailsofiron2': '2096980',
            'talesarise': '740130',
            'thecastingoffrankstone': '2516790',
            'thecosmicwheelsisterhood': '1340480',
            'thedarkness': '2619150',
            'thedarkness2': '2619150',
            'theescapists2': '641990',
            'thejackboxpartypack7': '1211630',
            'thelegendofheroes': '1668540',
            'thelegendofherostrailstoazure': '1668540',
            'thelifeandsufferingofsirbrante': '1272160',
            'thepathless': '1492680',
            'thepluckysquire': '1472550',
            'theprecinct': '1434660',
            'thespiritandthemouse': '1592750',
            'thewildatheart': '1093290',
            'threeminutestoeight': '1783350',
            'tinytinasassaultondragonkeep': '1712840',
            'troversavestheuniversee': '1051200',
            'troversavestheUniverse': '1051200',
            'unsighted': '1062110',
            'underthewaves': '1513960',
            'unrulyheroes': '780350',
            'wizardwithagun': '1150530',
            'yokusislandexpress': '334940',
            'ysixmonstrumnox': '1112740',
            'zootycoon': '613880',
            'loreleiandthelasereyes': '2008920',
            'taintedgrailtethefallofavalon': '1335640',
            'taintedgrail': '1335640',
            'burnoutparadiseremastered': '1238080',
            'cardshark': '1141190',
            'cassettebeasts': '1321440',
            'chicoryacolorfultale': '1123450',
            'citizensleeper': '1578650',
            'crimebossrockycity': '1897660',
            'crimebossrockacity': '1897660',
            'crimecsv': '2710450',
            'deathbound': '1952860',
            'deathsquared': '471810',
            'degreesofseparation': '809880',
            'deusexinvisiblewar': '6920',
            'devilmacry': '220440',
            'devilmacryhdcollection': '631510',
            'doubledragoneon': '252350',
            'doubledragonneon': '252350',
            'dragonballfighterz': '678950',
            'dragonballsparkingzero': '1790600',
            'dragonballxenoverse2': '454650',
            'dragonballzkakarot': '851850',
            'dungeonsofhinterberg': '1983580',
            'duneimperium': '2369250',
            'eldenringshadowoftheerdtree': '1245620',
            'eldersouls': '1685510',
            'enadeambbq': '2595810',
            'enadreambbq': '2595810',
            'engarde': '1264450',
            'enderalforgotstories': '933480',
            'enderalforgottenstories': '933480',
            'fahrenheit': '21620',
            'fahrenheitindigoprophecy': '21620',
            'falloutlondon': '2475270',
            'farchangingtides': '1570010',
            'flatoutheroes': '2508460',
            'blazbluecentralfiction': '586140',
            'arcrunner': '2170000',
            'assassinscreedbrotherhood': '2927440',
            'astralascent': '1668150',
            'atelieryumia': '2849710',
            'battletoads': '1380090',
            'bayonetta3': '2742550',
            'bendyandthedarkrevival': '1621490',
            'blanc': '1902330',
            'brightmemoryinfinite': '1178830',
            'bugfablestheeverlastingsakling': '1082710',
            'bugfablesteheverlastin': '1082710',
            'sonicxshadowgenerations': '2513280',
            'pokemonscarletviolet': '2710140',
            'pokemonswordshield': '2710130',
            'pokemonbrilliantdiamondandshiningpearl': '2710120',
            'researchanddestroy': '1538880',
            'spintiresmudrunner': '675010',
            // Additional games - comprehensive mapping
            'alanwake2': '2972660',
            'alanwakeremastered': '108710',
            'alienisolation': '214490',
            'amnesia': '57300',
            'amnesiathedarkedescent': '57300',
            'amnesiarebirth': '999220',
            'amnesiathedungeon': '1944430',
            'arcanumofsteamworks': '500810',
            'ariseofthegoldenidol': '2877760',
            'arkkingdomascended': '2399830',
            'arksurvivalascended': '2399830',
            'armoredsoul': '2530500',
            'ashenonefall': '1945800',
            'ateliersophie': '527270',
            'backpackhero': '1970580',
            'baldursgate3': '1086940',
            'banishers': '1338840',
            'banishersghostsofneweden': '1338840',
            'batmankarkhamknight': '208650',
            'batmanarkhamcity': '200260',
            'batmanarkhamasylum': '35140',
            'bioshock': '7670',
            'bioshockinfinite': '8870',
            'bioshockremastered': '409710',
            'blackmyth': '2358720',
            'blackmythwukong': '2358720',
            'blasphemous': '774361',
            'blasphemous2': '2114740',
            'bloodstained': '692850',
            'bloodstainedritualofthenight': '692850',
            'bodyofdoubt': '2899160',
            'borderlands3': '397540',
            'brotato': '1942280',
            'callofcthulhu': '399810',
            'carrion': '953490',
            'castlevania': '2720030',
            'celeste': '504230',
            'childrenofthesun': '1865940',
            'chronoark': '1440530',
            'civilizationvii': '1295660',
            'codevein': '678960',
            'controlultimatedition': '870780',
            'coralisland': '1158160',
            'cosmicshake': '1766230',
            'crisiscore': '1608080',
            'crosscode': '368340',
            'cuffbust': '2950320',
            'cultofthelamb': '1313140',
            'curseofthedead': '1766740',
            'curseofthedeadgods': '1766740',
            'darkanddarker': '2016590',
            'darkestdungeon': '262060',
            'darkestdungeon2': '1940340',
            'darksiders': '50620',
            'darksidersgenesis': '710920',
            'darksiders3': '606280',
            'deadcells': '588650',
            'deadisland2': '934700',
            'deadspace': '1693980',
            'deadspaceremake': '1693980',
            'deathmustdie': '2334730',
            'deathloop': '1252330',
            'deathsdoor': '894020',
            'deliver us the moon': '428660',
            'deltarune': '1671210',
            'desperados3': '610370',
            'devilmaycry5': '601150',
            'diabloiv': '2344520',
            'disco elysium': '632470',
            'discoelysium': '632470',
            'dishonored': '205100',
            'dishonored2': '403640',
            'divinity': '435150',
            'divinityoriginalsin2': '435150',
            'dragonage': '1845910',
            'dragonagetheveilguard': '1845910',
            'dragonsdogma2': '2054970',
            'dredge': '1562430',
            'dungeonmunchies': '799640',
            'dustborn': '721180',
            'dyinglight': '239140',
            'dyinglight2': '534380',
            'earthfall': '1903340',
            'elderscrolls': '22330',
            'elex': '411300',
            'elex2': '900040',
            'endzone': '933820',
            'escapefromtarkov': '2625280',
            'evilwest': '1065310',
            'exoprimal': '1286220',
            'expeditions': '2193470',
            'fableanniversary': '288470',
            'fallguys': '1097150',
            'fallout4': '377160',
            'farcry5': '552520',
            'farcry6': '2369390',
            'fatesamurairemnant': '1814070',
            'finalfantasy7remake': '2909400',
            'finalfantasy7rebirth': '2909400',
            'finalfantasyvirebirth': '2909400',
            'finalfantasy16': '2515020',
            'finalfantasyxvi': '2515020',
            'firewatch': '383870',
            'flushfire': '2826440',
            'frostpunk': '323190',
            'frostpunk2': '2098160',
            'ghostoftsushima': '2215430',
            'ghostrunner': '1139900',
            'ghostrunner2': '2144740',
            'godsunchained': '2082350',
            'godofwar': '1593500',
            'godofwarragnarok': '2322010',
            'gollum': '1265780',
            'gothamknights': '1496790',
            'grandtheftauto5': '271590',
            'gta5': '271590',
            'graveyardkeeper': '599140',
            'greedfallgoldedition': '606880',
            'greedfall': '606880',
            'grisfinale': '1209990',
            'guiltygear': '1384160',
            'guiltygearstrive': '1384160',
            'hades': '1145360',
            'hades2': '1145350',
            'haiku': '1331510',
            'haloinfinite': '1240440',
            'handofmerlin': '1338440',
            'hardwest2': '1282730',
            'harvestmoon': '1061060',
            'haveanicedeath': '1740720',
            'helldivers2': '553850',
            'hellblade': '414340',
            'hellbladesenuassacrifice': '414340',
            'highonlife': '1583230',
            'hitman': '1659040',
            'hitman3': '1659040',
            'hogwartslegacy': '990080',
            'hollowknight': '367520',
            'hollowknightsilksong': '1030300',
            'horizonforbiddenwest': '2420110',
            'horizonzerodawn': '1151640',
            'hotlinemiami': '219150',
            'houseofashes': '1281590',
            'huntshowdown': '770720',
            'hyenas': '2127570',
            'hyperbolica': '1256230',
            'immortalityoftheexile': '2694490',
            'inscryption': '1092790',
            'ittkr': '2604220',
            'ittakestworesurrected': '2604220',
            'ittakestwo': '1426210',
            'jedi': '1774580',
            'jedisurvivor': '1774580',
            'jurassicworldevolution': '648350',
            'justcause3': '225540',
            'justcause4': '517630',
            'katanakami': '1092790',
            'kenshi': '233860',
            'kingdomhearts': '2552430',
            'lastepoch': '899770',
            'lastofus': '1888930',
            'lastofuspart1': '1888930',
            'legendofzeldabreathofthewild': '2710100',
            'legendofzeldatearsofthekingdom': '2710110',
            'liesofp': '1627720',
            'limbo': '48000',
            'littlenightmares': '424840',
            'littlenightmares2': '860510',
            'lordoftherings': '2933620',
            'maneater': '629820',
            'marioplusrabbids': '2710200',
            'masseffect': '1328670',
            'masseffectlegendaryedition': '1328670',
            'medievaldynasty': '1129580',
            'metroidprimeremasters': '2710070',
            'microsoftflightsimulator': '1250410',
            'monsterhunterworld': '582010',
            'monsterhunterwilds': '2685950',
            'moonlighter': '606150',
            'muonraysinterstellar': '2455170',
            'musedash': '774171',
            'necrobarista': '725270',
            'needforspeed': '1846380',
            'needforspeedunbound': '1846380',
            'nierautomata': '524220',
            'nierreplicant': '1113560',
            'nightinthewoods': '481510',
            'noita': '881100',
            'octopathtraveler': '921570',
            'octopathtraveler2': '1971680',
            'oddworld': '945950',
            'okami': '587620',
            'onepiece': '1301530',
            'onepieceodyssey': '1301530',
            'ori': '387290',
            'oriandtheblindforest': '387290',
            'oriandthewillofthewisps': '1057090',
            'outerwilds': '753640',
            'outerworlds': '578650',
            'overwatch2': '2357570',
            'pacificdrive': '1458140',
            'palworld': '1623730',
            'payday3': '1272080',
            'pentiment': '1205520',
            'persona3reload': '2161700',
            'persona4golden': '1113000',
            'persona5royal': '1687950',
            'pillarsofeternity2': '560130',
            'plaguetale': '752590',
            'plaguetaleinnoncence': '752590',
            'plaguetalerequiem': '1182900',
            'prey': '480490',
            'primordia': '227000',
            'psychonauts2': '607080',
            'ratchetandclank': '1895880',
            'ravenbound': '1381550',
            'readyornot': '1144200',
            'reddeadredemption': '1174180',
            'reddeadredemption2': '1174180',
            'remnant': '617290',
            'remnant2': '1282100',
            'residentevil4': '2050650',
            'residentevil4remake': '2050650',
            'residentevilvillage': '1196590',
            'returnal': '1649240',
            'riddickbrightfalls': '2860290',
            'rimworld': '294100',
            'riskofrain2': '632360',
            'robocop': '1681430',
            'roguelegacy2': '1253060',
            'rysesonofrome': '302510',
            'satisfactory': '526870',
            'scarlethollow': '1419560',
            'sekiro': '814380',
            'shadowgambit': '1545560',
            'shadowofthetombraider': '750920',
            'sherlock': '2369680',
            'sherlockholmes': '2369680',
            'skyrim': '489830',
            'slaytheprincesses': '1989830',
            'slaytheprince': '1989830',
            'slaythespire': '646570',
            'slaythespire2': '2868840',
            'sniperelite5': '1029690',
            'sonicfrontiers': '1237320',
            'sonicgenerations': '71340',
            'spacemarine2': '2183900',
            'spiderman': '1817070',
            'spidermanremastered': '1817070',
            'spidermanmilesmorales': '1817190',
            'spiritfarer': '972660',
            'stardewvalley': '413150',
            'steelrising': '1283400',
            'stellarblade': '2177030',
            'strangerofparadise': '1399880',
            'strandeddeep': '313120',
            'streetfighter6': '1364780',
            'subnautica': '264710',
            'subnauticabelowzero': '848450',
            'sunsetoverdrive': '847370',
            'superliminal': '1049410',
            'tekken8': '1778820',
            'terraria': '105600',
            'thebindingofisaac': '250900',
            'thecallisto protocol': '1274920',
            'thecallistoprotocol': '1274920',
            'thechant': '1599940',
            'thecourier': '1939460',
            'theeviljudaswithin': '1975870',
            'theevilwithin': '268050',
            'theevilwithin2': '601430',
            'theforest': '242760',
            'thelastfaith': '1529190',
            'thelastofuspart2': '2531650',
            'themedium': '1293160',
            'thequarry': '1456400',
            'thewitcher3': '292030',
            'titanfall2': '1237970',
            'torchlight': '1310670',
            'torchlightinfinite': '1310670',
            'tormentedsouls': '1367590',
            'totalk': '2317610',
            'totalwarhammer3': '1142710',
            'tunic': '553420',
            'turtlebeach': '1291440',
            'unpacking': '1135690',
            'untilthenight': '2877040',
            'untilthen': '1341820',
            'vampiresurvivors': '1794680',
            'valheim': '892970',
            'venba': '1491670',
            'vigil': '1557270',
            'vigilthelangsandmonumentguard': '1557270',
            'walkingdead': '207610',
            'warframe': '230410',
            'wasteland3': '719040',
            'weirdwest': '1097350',
            'wildhearts': '1938010',
            'witchfire': '1399650',
            'witness': '210970',
            'wolfenstein': '201810',
            'wolfenstein2': '612880',
            'xenobladechronicles': '2710050',
            'yakuza3remastered': '1088710',
            'yakuza4remastered': '1105500',
            'zombiearmy4': '694280',
        };
    }

    // Steam cover image cache (persisted in localStorage)
    getSteamCoverCache() {
        if (!this._steamCoverCache) {
            try {
                this._steamCoverCache = JSON.parse(localStorage.getItem('steamCoverCache') || '{}');
                // Cache version - invalidate stale 'none' entries when known IDs are updated
                const cacheVersion = 'v3';
                if (this._steamCoverCache._version !== cacheVersion) {
                    // Clear all 'none' entries so games with newly added known IDs get re-fetched
                    const knownIds = this.getKnownSteamAppIds();
                    for (const key of Object.keys(this._steamCoverCache)) {
                        if (this._steamCoverCache[key] === 'none' ||
                            (knownIds[key] && this._steamCoverCache[key] !== `https://cdn.cloudflare.steamstatic.com/steam/apps/${knownIds[key]}/library_600x900_2x.jpg`)) {
                            delete this._steamCoverCache[key];
                        }
                    }
                    this._steamCoverCache._version = cacheVersion;
                    this.saveSteamCoverCache();
                }
            } catch (e) {
                this._steamCoverCache = {};
            }
        }
        return this._steamCoverCache;
    }

    saveSteamCoverCache() {
        try {
            localStorage.setItem('steamCoverCache', JSON.stringify(this._steamCoverCache));
        } catch (e) { /* ignore quota errors */ }
    }

    // Split concatenated game names into words for better search
    // Smart name normalization for concatenated game names before HLTB lookup
    normalizeGameName(name) {
        if (!name) return name;

        let result = name;

        // Handle ALL_CAPS_WITH_UNDERSCORES: "STAR_WARS_GAME" → "Star Wars Game"
        if (/^[A-Z0-9_]+$/.test(result)) {
            result = result
                .replace(/_/g, ' ')
                .toLowerCase()
                .replace(/\b\w/g, c => c.toUpperCase());
        }

        // Replace underscores/hyphens with spaces
        result = result.replace(/[_-]/g, ' ');

        // Convert camelCase to spaces: "StarWars" → "Star Wars"
        result = result.replace(/([a-z])([A-Z])/g, '$1 $2');

        // Handle consecutive capitals followed by lowercase: "HLTBGame" → "HLTB Game"
        result = result.replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2');

        // Strip extra punctuation (keep apostrophes, colons, ampersands)
        result = result.replace(/[^\w\s':&!?-]/g, ' ');

        // Clean up extra whitespace
        result = result.replace(/\s+/g, ' ').trim();

        // If result is single word and looks concatenated, delegate to splitGameName
        if (!result.includes(' ')) {
            result = this.splitGameName(result);
        }

        return result;
    }

    splitGameName(name) {
        // Common game-related words to help split concatenated names
        const commonWords = [
            'the', 'of', 'and', 'in', 'on', 'at', 'to', 'for', 'is', 'a', 'an',
            'with', 'from', 'by', 'not', 'no', 'or', 'vs', 'all',
            'dark', 'dead', 'death', 'new', 'old', 'last', 'first', 'final',
            'super', 'ultra', 'mega', 'hyper', 'mini', 'micro',
            'night', 'day', 'dawn', 'dusk', 'shadow', 'light', 'fire', 'ice',
            'star', 'moon', 'sun', 'storm', 'wind', 'rain', 'snow', 'thunder',
            'king', 'queen', 'prince', 'lord', 'knight', 'dragon', 'demon',
            'sword', 'blade', 'shield', 'gun', 'war', 'battle', 'fight',
            'world', 'land', 'island', 'city', 'town', 'castle', 'tower',
            'road', 'path', 'way', 'gate', 'door', 'bridge', 'edge',
            'game', 'quest', 'tales', 'legend', 'legends', 'saga', 'story',
            'rise', 'fall', 'lost', 'found', 'hunt', 'hunter', 'craft',
            'red', 'blue', 'green', 'black', 'white', 'gold', 'silver',
            'iron', 'steel', 'stone', 'bone', 'blood', 'soul', 'souls',
            'doom', 'tomb', 'hell', 'haven', 'heaven', 'void', 'abyss',
            'silent', 'silence', 'wild', 'grind', 'split', 'deep', 'high',
            'zero', 'one', 'two', 'three', 'four', 'five', 'six',
            'survivor', 'survivors', 'remastered', 'remake', 'edition',
            'definitive', 'ultimate', 'deluxe', 'complete', 'collection',
            'shop', 'corner', 'shift', 'delivery', 'deadline', 'formula',
            'fort', 'solis', 'heretic', 'fork', 'royal', 'revolt',
            'artisan', 'banished', 'watching', 'rat', 'line', 'dice',
            'mirror', 'catalyst', 'kaiju', 'crack', 'magic',
            'ages', 'age', 'pirate', 'pirates', 'caribbean', 'dagger',
            'expedition', 'schedule', 'obscur', 'clair',
            'lego', 'sonic', 'mario', 'zelda', 'pokemon',
            'assassin', 'creed', 'fantasy', 'resident', 'evil',
            'metal', 'gear', 'solid', 'monster', 'hunter'
        ];

        let result = name
            .replace(/([a-z])([A-Z])/g, '$1 $2')
            .replace(/(\d+)/g, ' $1 ')
            .replace(/\s+/g, ' ')
            .trim();

        // If the name is already multi-word, return as is
        if (result.includes(' ') && result.split(' ').length > 2) {
            return result;
        }

        // Try to split concatenated lowercase words
        const lower = result.toLowerCase();
        const words = [];
        let remaining = lower;

        while (remaining.length > 0) {
            let found = false;
            // Try longest match first (up to 15 chars)
            for (let len = Math.min(15, remaining.length); len >= 2; len--) {
                const candidate = remaining.substring(0, len);
                if (commonWords.includes(candidate) && (remaining.length === len || remaining.length - len >= 2)) {
                    words.push(candidate);
                    remaining = remaining.substring(len);
                    found = true;
                    break;
                }
            }
            if (!found) {
                // Take a chunk until we find the next known word
                let nextWordStart = remaining.length;
                for (let i = 2; i < remaining.length; i++) {
                    const sub = remaining.substring(i);
                    for (const word of commonWords) {
                        if (sub.startsWith(word) && (sub.length === word.length || sub.length - word.length >= 2)) {
                            nextWordStart = i;
                            break;
                        }
                    }
                    if (nextWordStart < remaining.length) break;
                }
                words.push(remaining.substring(0, nextWordStart));
                remaining = remaining.substring(nextWordStart);
            }
        }

        return words.join(' ');
    }

    // Search Steam for a game and return cover image URL
    async fetchSteamCoverUrl(gameName) {
        const cache = this.getSteamCoverCache();
        const cacheKey = gameName.toLowerCase().replace(/[^a-z0-9]/g, '');

        // Check cache first
        if (cache[cacheKey]) {
            return cache[cacheKey] === 'none' ? null : cache[cacheKey];
        }

        // Check known Steam App ID mapping first
        const knownIds = this.getKnownSteamAppIds();
        const knownAppId = knownIds[cacheKey];
        if (knownAppId && knownAppId.match(/^\d+$/)) {
            // Use cover URL directly - Steam CDN is reliable, avoid CORS HEAD check
            const coverUrl = `https://cdn.cloudflare.steamstatic.com/steam/apps/${knownAppId}/library_600x900_2x.jpg`;
            cache[cacheKey] = coverUrl;
            this.saveSteamCoverCache();
            return coverUrl;
        }

        // Clean up the game name for better search results
        // Handle concatenated names like "doomthedarkages" -> "doom the dark ages"
        const searchName = this.splitGameName(gameName);

        const corsProxies = [
            'https://corsproxy.io/?',
            'https://api.allorigins.win/raw?url=',
            'https://api.codetabs.com/v1/proxy?quest='
        ];

        for (const proxy of corsProxies) {
            try {
                const searchUrl = `https://store.steampowered.com/api/storesearch/?term=${encodeURIComponent(searchName)}&l=english&cc=US`;
                const response = await fetch(proxy + encodeURIComponent(searchUrl), {
                    signal: AbortSignal.timeout(8000)
                });
                const data = await response.json();

                if (data && data.items && data.items.length > 0) {
                    const appId = data.items[0].id;
                    // Use portrait cover directly - Steam CDN is reliable
                    const coverUrl = `https://cdn.cloudflare.steamstatic.com/steam/apps/${appId}/library_600x900_2x.jpg`;
                    cache[cacheKey] = coverUrl;
                    this.saveSteamCoverCache();
                    return coverUrl;
                }
            } catch (e) {
                continue;
            }
        }

        // Fallback: Try RAWG API for games not found on Steam
        const rawgUrl = await this.fetchRawgCoverUrl(searchName, corsProxies);
        if (rawgUrl) {
            cache[cacheKey] = rawgUrl;
            this.saveSteamCoverCache();
            return rawgUrl;
        }

        // Fallback: Try with simplified name (remove common suffixes)
        const simplifiedName = searchName
            .replace(/\b(remastered|remake|edition|definitive|ultimate|deluxe|complete|collection|hd|goty)\b/gi, '')
            .replace(/\b(ii|iii|iv|v|vi|vii|viii|ix|x)\b$/gi, '')
            .trim();
        if (simplifiedName !== searchName && simplifiedName.length > 3) {
            for (const proxy of corsProxies) {
                try {
                    const searchUrl = `https://store.steampowered.com/api/storesearch/?term=${encodeURIComponent(simplifiedName)}&l=english&cc=US`;
                    const response = await fetch(proxy + encodeURIComponent(searchUrl), {
                        signal: AbortSignal.timeout(8000)
                    });
                    const data = await response.json();
                    if (data && data.items && data.items.length > 0) {
                        const appId = data.items[0].id;
                        const coverUrl = `https://cdn.cloudflare.steamstatic.com/steam/apps/${appId}/library_600x900_2x.jpg`;
                        cache[cacheKey] = coverUrl;
                        this.saveSteamCoverCache();
                        return coverUrl;
                    }
                } catch (e) { continue; }
            }
        }

        // Fallback Strategy 1: Try original game name as-is (no splitting)
        if (gameName !== searchName) {
            for (const proxy of corsProxies) {
                try {
                    const searchUrl = `https://store.steampowered.com/api/storesearch/?term=${encodeURIComponent(gameName)}&l=english&cc=US`;
                    const response = await fetch(proxy + encodeURIComponent(searchUrl), {
                        signal: AbortSignal.timeout(8000)
                    });
                    const data = await response.json();
                    if (data && data.items && data.items.length > 0) {
                        const appId = data.items[0].id;
                        const coverUrl = `https://cdn.cloudflare.steamstatic.com/steam/apps/${appId}/library_600x900_2x.jpg`;
                        cache[cacheKey] = coverUrl;
                        this.saveSteamCoverCache();
                        return coverUrl;
                    }
                } catch (e) { continue; }
            }
        }

        // Fallback Strategy 2: Split camelCase (e.g. "StarWars" -> "Star Wars")
        const camelCaseSplit = gameName.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2').trim();
        if (camelCaseSplit !== gameName && camelCaseSplit !== searchName) {
            for (const proxy of corsProxies) {
                try {
                    const searchUrl = `https://store.steampowered.com/api/storesearch/?term=${encodeURIComponent(camelCaseSplit)}&l=english&cc=US`;
                    const response = await fetch(proxy + encodeURIComponent(searchUrl), {
                        signal: AbortSignal.timeout(8000)
                    });
                    const data = await response.json();
                    if (data && data.items && data.items.length > 0) {
                        const appId = data.items[0].id;
                        const coverUrl = `https://cdn.cloudflare.steamstatic.com/steam/apps/${appId}/library_600x900_2x.jpg`;
                        cache[cacheKey] = coverUrl;
                        this.saveSteamCoverCache();
                        return coverUrl;
                    }
                } catch (e) { continue; }
            }
            // Also try RAWG with camelCase split name
            const rawgCamel = await this.fetchRawgCoverUrl(camelCaseSplit, corsProxies);
            if (rawgCamel) {
                cache[cacheKey] = rawgCamel;
                this.saveSteamCoverCache();
                return rawgCamel;
            }
        }

        // Fallback Strategy 3: Add spaces before capital letters (e.g. "StarWarsBattlefront" -> "Star Wars Battlefront")
        const spacedCapitals = gameName.replace(/([A-Z])/g, ' $1').replace(/\s+/g, ' ').trim();
        if (spacedCapitals !== gameName && spacedCapitals !== camelCaseSplit && spacedCapitals !== searchName) {
            for (const proxy of corsProxies) {
                try {
                    const searchUrl = `https://store.steampowered.com/api/storesearch/?term=${encodeURIComponent(spacedCapitals)}&l=english&cc=US`;
                    const response = await fetch(proxy + encodeURIComponent(searchUrl), {
                        signal: AbortSignal.timeout(8000)
                    });
                    const data = await response.json();
                    if (data && data.items && data.items.length > 0) {
                        const appId = data.items[0].id;
                        const coverUrl = `https://cdn.cloudflare.steamstatic.com/steam/apps/${appId}/library_600x900_2x.jpg`;
                        cache[cacheKey] = coverUrl;
                        this.saveSteamCoverCache();
                        return coverUrl;
                    }
                } catch (e) { continue; }
            }
        }

        // Fallback Strategy 4: Try partial matches (first 2-3 words of the processed name)
        const nameWords = searchName.split(' ').filter(w => w.length > 1);
        const partialVariants = [];
        if (nameWords.length > 3) partialVariants.push(nameWords.slice(0, 3).join(' '));
        if (nameWords.length > 2) partialVariants.push(nameWords.slice(0, 2).join(' '));
        for (const partial of partialVariants) {
            if (partial.length < 4) continue;
            let found = false;
            for (const proxy of corsProxies) {
                try {
                    const searchUrl = `https://store.steampowered.com/api/storesearch/?term=${encodeURIComponent(partial)}&l=english&cc=US`;
                    const response = await fetch(proxy + encodeURIComponent(searchUrl), {
                        signal: AbortSignal.timeout(8000)
                    });
                    const data = await response.json();
                    if (data && data.items && data.items.length > 0) {
                        const appId = data.items[0].id;
                        const coverUrl = `https://cdn.cloudflare.steamstatic.com/steam/apps/${appId}/library_600x900_2x.jpg`;
                        cache[cacheKey] = coverUrl;
                        this.saveSteamCoverCache();
                        return coverUrl;
                    }
                } catch (e) { continue; }
            }
            if (found) break;
        }

        // Mark as not found so we don't search again
        cache[cacheKey] = 'none';
        this.saveSteamCoverCache();
        return null;
    }

    // Fetch cover image from RAWG API as fallback
    async fetchRawgCoverUrl(searchName, corsProxies) {
        for (const proxy of corsProxies) {
            try {
                const rawgSearchUrl = `https://api.rawg.io/api/games?key=c542e67aec3a4340908f9de9e86038af&search=${encodeURIComponent(searchName)}&page_size=1`;
                const response = await fetch(proxy + encodeURIComponent(rawgSearchUrl), {
                    signal: AbortSignal.timeout(8000)
                });
                const data = await response.json();
                if (data && data.results && data.results.length > 0 && data.results[0].background_image) {
                    return data.results[0].background_image;
                }
            } catch (e) { continue; }
        }
        return null;
    }

    lazyLoadImages() {
        const images = document.querySelectorAll('img[data-src]');

        // Fallback for browsers that don't support IntersectionObserver
        if (!('IntersectionObserver' in window)) {
            images.forEach(img => {
                img.src = img.dataset.src;
                img.classList.add('loaded');
            });
            return;
        }

        const observer = new IntersectionObserver((entries) => {
            entries.forEach(entry => {
                if (entry.isIntersecting) {
                    const img = entry.target;

                    // Create a new image to preload
                    const tempImg = new Image();
                    tempImg.onload = () => {
                        img.src = img.dataset.src;
                        img.classList.add('loaded');

                        // Remove shimmer effect from parent
                        const container = img.closest('.image-container');
                        if (container) {
                            container.style.setProperty('--shimmer-display', 'none');
                        }
                    };
                    tempImg.onerror = async () => {
                        // Local image not found - try fetching from Steam/RAWG
                        const gameName = img.alt || img.dataset.src.replace('images/', '').replace('.png', '');
                        try {
                            const coverUrl = await window.gameLibrary.fetchSteamCoverUrl(gameName);
                            if (coverUrl) {
                                const coverImg = new Image();
                                coverImg.onload = () => {
                                    img.src = window.gameLibrary.getDisplayImageUrl(coverUrl);
                                    img.classList.add('loaded');
                                    const container = img.closest('.image-container');
                                    if (container) {
                                        container.style.setProperty('--shimmer-display', 'none');
                                    }
                                };
                                coverImg.onerror = () => {
                                    // If library_600x900 fails, try header format
                                    if (coverUrl.includes('library_600x900')) {
                                        const headerUrl = coverUrl.replace('library_600x900_2x.jpg', 'header.jpg');
                                        const headerImg = new Image();
                                        headerImg.onload = () => {
                                            img.src = window.gameLibrary.getDisplayImageUrl(headerUrl);
                                            img.classList.add('loaded');
                                            const container = img.closest('.image-container');
                                            if (container) {
                                                container.style.setProperty('--shimmer-display', 'none');
                                            }
                                            // Update cache with working URL
                                            const cache = window.gameLibrary.getSteamCoverCache();
                                            const cacheKey = gameName.toLowerCase().replace(/[^a-z0-9]/g, '');
                                            cache[cacheKey] = headerUrl;
                                            window.gameLibrary.saveSteamCoverCache();
                                        };
                                        headerImg.onerror = () => {
                                            img.classList.add('loaded');
                                        };
                                        headerImg.src = headerUrl;
                                    } else {
                                        img.classList.add('loaded');
                                    }
                                };
                                coverImg.src = coverUrl;
                                return;
                            }
                        } catch (e) {
                            // Cover lookup failed, keep placeholder
                        }
                        img.classList.add('loaded');
                    };
                    tempImg.src = img.dataset.src;

                    observer.unobserve(img);
                }
            });
        }, {
            rootMargin: '100px',
            threshold: 0.1
        });

        images.forEach(img => observer.observe(img));
    }

    // ============================================================
    // TAG SYSTEM
    // ============================================================

    // Predefined tag palette
    get TAG_PALETTE() {
        return [
            'RPG', 'Action', 'Adventure', 'Puzzle', 'Strategy',
            'Shooter', 'Platformer', 'Horror', 'Sports', 'Racing',
            'Simulation', 'Fighting', 'Indie', 'Retro', 'Multiplayer',
            'Story-Rich', 'Open World', 'Stealth', 'Survival', 'Casual'
        ];
    }

    loadGameTags() {
        try {
            const saved = localStorage.getItem('gameLibraryTags');
            if (!saved) return {};
            const raw = JSON.parse(saved);
            // raw is { gameId: ['tag1','tag2'] }
            return raw;
        } catch {
            return {};
        }
    }

    saveGameTags() {
        localStorage.setItem('gameLibraryTags', JSON.stringify(this.gameTags));
    }

    getGameTags(gameId) {
        return this.gameTags[gameId] || [];
    }

    toggleGameTag(gameId, tag) {
        if (!this.gameTags[gameId]) {
            this.gameTags[gameId] = [];
        }
        const idx = this.gameTags[gameId].indexOf(tag);
        if (idx === -1) {
            this.gameTags[gameId].push(tag);
        } else {
            this.gameTags[gameId].splice(idx, 1);
        }
        this.saveGameTags();
    }

    setActiveTagFilter(tag) {
        this.activeTagFilter = (this.activeTagFilter === tag) ? null : tag;
        this.updateTagFilterBar();
        this.filterAndRender();
    }

    clearTagFilter() {
        this.activeTagFilter = null;
        this.updateTagFilterBar();
        this.filterAndRender();
    }

    updateTagFilterBar() {
        const bar = document.getElementById('tagFilterBar');
        if (!bar) return;
        bar.querySelectorAll('.tag-filter-chip').forEach(btn => {
            btn.classList.toggle('active', btn.dataset.tag === this.activeTagFilter);
        });
        const clearBtn = document.getElementById('tagFilterClear');
        if (clearBtn) {
            clearBtn.style.display = this.activeTagFilter ? 'inline-flex' : 'none';
        }
    }

    renderTagFilterBar() {
        const bar = document.getElementById('tagFilterBar');
        if (!bar) return;
        bar.innerHTML = this.TAG_PALETTE.map(tag =>
            `<button class="tag-filter-chip${this.activeTagFilter === tag ? ' active' : ''}" data-tag="${tag}" title="Filter by ${tag}">${tag}</button>`
        ).join('') +
        `<button id="tagFilterClear" class="tag-filter-clear" style="display:${this.activeTagFilter ? 'inline-flex' : 'none'}" title="Clear tag filter">✕ Clear</button>`;

        bar.querySelectorAll('.tag-filter-chip').forEach(btn => {
            btn.addEventListener('click', () => this.setActiveTagFilter(btn.dataset.tag));
        });
        const clearBtn = document.getElementById('tagFilterClear');
        if (clearBtn) clearBtn.addEventListener('click', () => this.clearTagFilter());
    }

    _renderCardTags(gameId) {
        const tags = this.getGameTags(gameId);
        if (!tags.length) return '';
        return `<div class="card-tags">${tags.map(t =>
            `<span class="card-tag-chip${this.activeTagFilter === t ? ' active' : ''}" data-tag="${t}">${t}</span>`
        ).join('')}</div>`;
    }

    openTagEditor(gameId) {
        const game = this.games.find(g => g.id === gameId);
        if (!game) return;
        const currentTags = this.getGameTags(gameId);

        // Build modal HTML
        const overlay = document.createElement('div');
        overlay.id = 'tagEditorOverlay';
        overlay.className = 'tag-editor-overlay';
        overlay.innerHTML = `
            <div class="tag-editor-panel">
                <button class="tag-editor-close" id="tagEditorClose">&times;</button>
                <h3>🏷️ Tags for <em>${game.name}</em></h3>
                <div class="tag-editor-chips">
                    ${this.TAG_PALETTE.map(tag =>
                        `<button class="tag-editor-chip${currentTags.includes(tag) ? ' selected' : ''}" data-tag="${tag}">${tag}</button>`
                    ).join('')}
                </div>
                <p class="tag-editor-hint">Click tags to toggle. Changes save instantly.</p>
            </div>
        `;
        document.body.appendChild(overlay);

        overlay.querySelectorAll('.tag-editor-chip').forEach(btn => {
            btn.addEventListener('click', () => {
                this.toggleGameTag(gameId, btn.dataset.tag);
                btn.classList.toggle('selected', this.getGameTags(gameId).includes(btn.dataset.tag));
                this.filterAndRender();
            });
        });

        const close = () => {
            overlay.remove();
        };
        document.getElementById('tagEditorClose').addEventListener('click', close);
        overlay.addEventListener('click', e => { if (e.target === overlay) close(); });
    }
}

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

// Initialize the app
document.addEventListener('DOMContentLoaded', () => {
    window.gameLibrary = new GameLibrary();
});
