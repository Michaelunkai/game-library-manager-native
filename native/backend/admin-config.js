// Netlify Function: admin-config
// Provides strongly-consistent server-side persistence for admin config.
// Netlify Blobs is the primary store; GitHub remains an optional legacy fallback.

const https = require('https');
const fs = require('fs');
const path = require('path');

const GITHUB_TOKEN = process.env.GITHUB_TOKEN || '';
const GITHUB_REPO = 'Michaelunkai/game-library-manager-web';
const ADMIN_TOKEN = process.env.ADMIN_TOKEN || 'glm-admin-2024';
const NETLIFY_SITE_ID = process.env.NETLIFY_SITE_ID || 'c8ccb88c-0b80-486f-940b-e89d9acefe99';
const NETLIFY_BLOBS_TOKEN = process.env.NETLIFY_BLOBS_TOKEN || process.env.NETLIFY_AUTH_TOKEN || '';

let blobStore = null;
try {
  const { getStore } = require('@netlify/blobs');
  const blobOptions = {
    name: 'game-library-admin-config',
    consistency: 'strong',
    fetch: require('./safe-fetch').createSafeFetch()
  };
  if (NETLIFY_SITE_ID && NETLIFY_BLOBS_TOKEN) {
    blobOptions.siteID = NETLIFY_SITE_ID;
    blobOptions.token = NETLIFY_BLOBS_TOKEN;
  }
  blobStore = getStore(blobOptions);
} catch (error) {
  console.warn('Netlify Blobs unavailable; GitHub fallback only:', error.message);
}

function readBundledDefaultConfig() {
  const candidates = [
    path.resolve(__dirname, '../../data/admin-config.json'),
    path.resolve(__dirname, '../../public/data/admin-config.json'),
    path.resolve(process.cwd(), 'data/admin-config.json'),
    path.resolve(process.cwd(), 'public/data/admin-config.json')
  ];
  for (const candidate of candidates) {
    try {
      const parsed = JSON.parse(fs.readFileSync(candidate, 'utf8'));
      if (parsed && typeof parsed === 'object') return parsed;
    } catch (error) {
      // The function bundle may not contain every candidate path.
    }
  }
  return null;
}

const BUNDLED_DEFAULT_CONFIG = readBundledDefaultConfig() || {};
const DEFAULT_CONFIG = {
  hiddenTabs: Array.isArray(BUNDLED_DEFAULT_CONFIG.hiddenTabs) ? BUNDLED_DEFAULT_CONFIG.hiddenTabs : [],
  gameCategories: BUNDLED_DEFAULT_CONFIG.gameCategories && typeof BUNDLED_DEFAULT_CONFIG.gameCategories === 'object'
    ? BUNDLED_DEFAULT_CONFIG.gameCategories
    : {},
  tabs: Array.isArray(BUNDLED_DEFAULT_CONFIG.tabs) ? BUNDLED_DEFAULT_CONFIG.tabs : null,
  lastUpdated: BUNDLED_DEFAULT_CONFIG.lastUpdated || 'bootstrap-v1'
};

// Both paths get updated simultaneously for redundancy
const GITHUB_CONFIG_PATHS = [
  'data/admin-config.json',
  'public/data/admin-config.json'
];

const CORS_HEADERS = {
  'Access-Control-Allow-Origin': '*',
  'Access-Control-Allow-Headers': 'Content-Type, X-Admin-Token, If-Match, Cache-Control, Pragma',
  'Access-Control-Allow-Methods': 'GET, POST, OPTIONS',
  'Cache-Control': 'no-cache, no-store, must-revalidate',
  'Pragma': 'no-cache'
};

function githubRequest(method, filePath, body) {
  return new Promise((resolve, reject) => {
    if (!GITHUB_TOKEN) {
      reject(new Error('GITHUB_TOKEN is not configured'));
      return;
    }
    const options = {
      hostname: 'api.github.com',
      path: `/repos/${GITHUB_REPO}/contents/${filePath}`,
      method: method,
      headers: {
        'Authorization': `token ${GITHUB_TOKEN}`,
        'User-Agent': 'game-library-netlify-function',
        'Accept': 'application/vnd.github.v3+json',
        'Content-Type': 'application/json'
      }
    };

    const req = https.request(options, (res) => {
      let data = '';
      res.on('data', chunk => data += chunk);
      res.on('end', () => {
        try {
          resolve({ status: res.statusCode, data: JSON.parse(data) });
        } catch (e) {
          resolve({ status: res.statusCode, data: data });
        }
      });
    });

    req.on('error', reject);
    if (body) req.write(JSON.stringify(body));
    req.end();
  });
}

async function readFromGitHub() {
  try {
    const result = await githubRequest('GET', GITHUB_CONFIG_PATHS[0]);
    if (result.status === 200 && result.data.content) {
      const content = Buffer.from(result.data.content, 'base64').toString('utf8');
      return { config: JSON.parse(content), sha: result.data.sha };
    }
  } catch (e) {
    console.error('GitHub read error:', e.message);
  }
  return { config: { ...DEFAULT_CONFIG }, sha: null };
}

function normalizeConfig(value) {
  const source = value && typeof value === 'object' ? value : {};
  return {
    hiddenTabs: Array.isArray(source.hiddenTabs) ? source.hiddenTabs : [],
    gameCategories: source.gameCategories && typeof source.gameCategories === 'object' ? source.gameCategories : {},
    tabs: Array.isArray(source.tabs) ? source.tabs : null,
    lastUpdated: source.lastUpdated || DEFAULT_CONFIG.lastUpdated
  };
}

async function readFromPersistence() {
  if (blobStore) {
    // Never turn an authoritative read failure into a stale fallback write.
    const entry = await blobStore.getWithMetadata('admin-config', { type: 'json', consistency: 'strong' });
    if (entry) return { config: normalizeConfig(entry.data), sha: entry.etag, etag: entry.etag, source: 'netlify-blobs' };
    const bootstrap = await readFromGitHub();
    return { ...bootstrap, sha: 'new:' + (bootstrap.sha || DEFAULT_CONFIG.lastUpdated), etag: null, source: 'netlify-blobs' };
  }
  const github = await readFromGitHub();
  return { ...github, source: 'github' };
}

async function writeToPersistence(data, current) {
  if (!blobStore || current.source !== 'netlify-blobs') return { success: false, statusCode: 503, source: current.source };
  const condition = current.etag ? { onlyIfMatch: current.etag } : { onlyIfNew: true };
  const result = await blobStore.setJSON('admin-config', data, condition);
  if (result.modified !== true) return { success: false, statusCode: 409, source: 'netlify-blobs' };
  if (typeof result.etag !== 'string' || !result.etag) throw new Error('Storage did not return a confirmed ETag');
  return { success: true, source: 'netlify-blobs', etag: result.etag };
}

// Keep simultaneous admin saves ordered so a slower request cannot overwrite a newer one.
let writeQueue = Promise.resolve();

async function writeToGitHub(data) {
  const content = Buffer.from(JSON.stringify(data, null, 2)).toString('base64');
  const message = `Admin config update - ${new Date().toISOString()}`;
  let anySuccess = false;

  for (const filePath of GITHUB_CONFIG_PATHS) {
    try {
      // Get current SHA
      const current = await githubRequest('GET', filePath);
      const sha = current.status === 200 ? current.data.sha : null;

      const body = { message, content, branch: 'main' };
      if (sha) body.sha = sha;

      const result = await githubRequest('PUT', filePath, body);
      if (result.status === 200 || result.status === 201) {
        console.log(`Saved to GitHub: ${filePath}`);
        anySuccess = true;
      } else {
        console.error(`GitHub write error for ${filePath}:`, result.status, JSON.stringify(result.data).substring(0, 200));
      }
    } catch (e) {
      console.error(`Failed to write ${filePath}:`, e.message);
    }
  }
  return anySuccess;
}

async function handle(event, context) {
  // Handle CORS preflight
  if (event.httpMethod === 'OPTIONS') {
    return { statusCode: 200, headers: CORS_HEADERS, body: '' };
  }

  if (event.httpMethod === 'GET') {
    const { config, sha, source } = await readFromPersistence();
    return {
      statusCode: 200,
      headers: CORS_HEADERS,
      body: JSON.stringify({
        success: true,
        config: config,
        configVersion: sha || config.lastUpdated || DEFAULT_CONFIG.lastUpdated,
        source,
        capabilities: { conditionalWrites: !!blobStore && source === 'netlify-blobs' }
      })
    };
  }

  if (event.httpMethod === 'POST') {
    // Validate admin token
    const token = event.headers['x-admin-token'] || event.headers['X-Admin-Token'];
    if (token !== ADMIN_TOKEN) {
      return {
        statusCode: 401,
        headers: CORS_HEADERS,
        body: JSON.stringify({ success: false, error: 'Unauthorized' })
      };
    }

    let payload;
    try {
      payload = JSON.parse(event.body);
    } catch (e) {
      return {
        statusCode: 400,
        headers: CORS_HEADERS,
        body: JSON.stringify({ success: false, error: 'Invalid JSON' })
      };
    }

    if (!payload || typeof payload !== 'object' || Array.isArray(payload)) {
      return { statusCode: 400, headers: CORS_HEADERS, body: JSON.stringify({ success: false, error: 'Invalid config object' }) };
    }
    const expected = payload.expectedVersion || event.headers['if-match'] || event.headers['If-Match'];
    if (typeof expected !== 'string' || !expected) {
      return { statusCode: 428, headers: CORS_HEADERS, body: JSON.stringify({ success: false, error: 'Reload the library before saving. A config version is required.' }) };
    }
    const operation = writeQueue.then(async () => {
      const currentResult = await readFromPersistence();
      if (expected !== currentResult.sha) return { success: false, statusCode: 409, source: currentResult.source };
      const current = normalizeConfig(currentResult.config);
      const data = normalizeConfig({
        ...current,
        hiddenTabs: Array.isArray(payload.hiddenTabs) ? payload.hiddenTabs : current.hiddenTabs,
        gameCategories: payload.gameCategories && typeof payload.gameCategories === 'object'
          ? { ...current.gameCategories, ...payload.gameCategories }
          : current.gameCategories,
        tabs: Array.isArray(payload.tabs) ? payload.tabs : current.tabs,
        lastUpdated: new Date().toISOString()
      });
      const saved = await writeToPersistence(data, currentResult);
      return { ...saved, data };
    });
    writeQueue = operation.catch(() => undefined);

    const { success, source, data, etag, statusCode } = await operation;

    if (success) {
      return {
        statusCode: 200,
        headers: CORS_HEADERS,
        body: JSON.stringify({
          success: true,
          message: `Config saved permanently to ${source}`,
          configVersion: etag || data.lastUpdated
        })
      };
    } else {
      return {
        statusCode: statusCode || 503,
        headers: CORS_HEADERS,
        body: JSON.stringify({ success: false, error: statusCode === 409 ? 'Configuration changed; local edits must be reconciled before retrying.' : 'Conditional storage is unavailable; no fallback write was attempted.' })
      };
    }
  }

  return {
    statusCode: 405,
    headers: CORS_HEADERS,
    body: JSON.stringify({ error: 'Method not allowed' })
  };
};

exports.handler = async (event, context) => {
  try { return await handle(event, context); }
  catch (error) {
    console.error('Admin config operation failed:', error.message);
    return { statusCode: 503, headers: CORS_HEADERS, body: JSON.stringify({ success: false, error: 'Storage is temporarily unavailable. Keep local edits and retry after reconnecting.' }) };
  }
};
