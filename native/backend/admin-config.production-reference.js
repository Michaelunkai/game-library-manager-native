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
    consistency: 'strong'
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
  'Access-Control-Allow-Headers': 'Content-Type, X-Admin-Token, Cache-Control, Pragma',
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
    try {
      const config = await blobStore.get('admin-config', { type: 'json', consistency: 'strong' });
      if (config) return { config: normalizeConfig(config), sha: config.lastUpdated || null, source: 'netlify-blobs' };
    } catch (error) {
      console.error('Netlify Blobs read error:', error.message);
    }
  }

  const github = await readFromGitHub();
  return { ...github, source: 'github' };
}

async function writeToPersistence(data) {
  if (blobStore) {
    try {
      const result = await blobStore.setJSON('admin-config', data);
      return { success: true, source: 'netlify-blobs', etag: result && result.etag };
    } catch (error) {
      console.error('Netlify Blobs write error:', error.message);
    }
  }

  return { success: await writeToGitHub(data), source: 'github' };
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

exports.handler = async (event, context) => {
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
        source
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

    const operation = writeQueue.then(async () => {
      const currentResult = await readFromPersistence();
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
      const saved = await writeToPersistence(data);
      return { ...saved, data };
    });
    writeQueue = operation.catch(() => undefined);

    const { success, source, data } = await operation;

    if (success) {
      return {
        statusCode: 200,
        headers: CORS_HEADERS,
        body: JSON.stringify({
          success: true,
          message: `Config saved permanently to ${source}`,
          configVersion: data.lastUpdated
        })
      };
    } else {
      return {
        statusCode: 500,
        headers: CORS_HEADERS,
        body: JSON.stringify({ success: false, error: `Failed to write config to ${source || 'server storage'}` })
      };
    }
  }

  return {
    statusCode: 405,
    headers: CORS_HEADERS,
    body: JSON.stringify({ error: 'Method not allowed' })
  };
};
