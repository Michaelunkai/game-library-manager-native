// Produces a deployment candidate from the exact inspected production function.
// It does not deploy, mutate storage, or edit the existing website.
const fs = require('node:fs');
const path = require('node:path');
let text = fs.readFileSync(path.join(__dirname, 'admin-config.production-reference.js'), 'utf8');
text = text.replace("consistency: 'strong'\n  };", "consistency: 'strong',\n    fetch: require('./safe-fetch').createSafeFetch()\n  };");
text = text.replace('Content-Type, X-Admin-Token, Cache-Control, Pragma', 'Content-Type, X-Admin-Token, If-Match, Cache-Control, Pragma');
const start = text.indexOf('async function readFromPersistence()');
const end = text.indexOf('// Keep simultaneous admin saves ordered', start);
if (start < 0 || end < 0) throw Error('Unexpected production source; review instead of patching blindly.');
text = text.slice(0, start) + `async function readFromPersistence() {
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

` + text.slice(end);
text = text.replace('exports.handler = async (event, context) => {', 'async function handle(event, context) {');
text = text.replace('        source\n', '        source,\n        capabilities: { conditionalWrites: !!blobStore && source === \'netlify-blobs\' }\n');
text = text.replace('    const operation = writeQueue.then(async () => {', `    if (!payload || typeof payload !== 'object' || Array.isArray(payload)) {
      return { statusCode: 400, headers: CORS_HEADERS, body: JSON.stringify({ success: false, error: 'Invalid config object' }) };
    }
    const expected = payload.expectedVersion || event.headers['if-match'] || event.headers['If-Match'];
    if (typeof expected !== 'string' || !expected) {
      return { statusCode: 428, headers: CORS_HEADERS, body: JSON.stringify({ success: false, error: 'Reload the library before saving. A config version is required.' }) };
    }
    const operation = writeQueue.then(async () => {`);
text = text.replace('      const current = normalizeConfig(currentResult.config);', `      if (expected !== currentResult.sha) return { success: false, statusCode: 409, source: currentResult.source };
      const current = normalizeConfig(currentResult.config);`);
text = text.replace('await writeToPersistence(data)', 'await writeToPersistence(data, currentResult)');
text = text.replace('const { success, source, data } = await operation;', 'const { success, source, data, etag, statusCode } = await operation;');
text = text.replace('configVersion: data.lastUpdated', 'configVersion: etag || data.lastUpdated');
text = text.replace('        statusCode: 500,', '        statusCode: statusCode || 503,');
text = text.replace('error: `Failed to write config to ${source || \'server storage\'}`', "error: statusCode === 409 ? 'Configuration changed; local edits must be reconciled before retrying.' : 'Conditional storage is unavailable; no fallback write was attempted.'");
text += `\nexports.handler = async (event, context) => {
  try { return await handle(event, context); }
  catch (error) {
    console.error('Admin config operation failed:', error.message);
    return { statusCode: 503, headers: CORS_HEADERS, body: JSON.stringify({ success: false, error: 'Storage is temporarily unavailable. Keep local edits and retry after reconnecting.' }) };
  }
};\n`;
fs.writeFileSync(path.join(__dirname, 'admin-config.js'), text);
console.log('Prepared conditional-write function. Deployment has not been performed.');
const receiptPath = path.join(__dirname, '../evidence/maintained-source.json');
let websitePath = path.join(__dirname, '../../public/app.js');
if (fs.existsSync(receiptPath)) {
  const receipt = JSON.parse(fs.readFileSync(receiptPath, 'utf8'));
  const expected = receipt.files.find(f => f.path === 'public/app.js').afterSha256;
  const actual = require('node:crypto').createHash('sha256').update(fs.readFileSync(websitePath)).digest('hex');
  if (actual !== expected) throw Error('Maintained website changed after preparation. Review it before regenerating.');
  websitePath = path.join(receipt.backupRoot, 'public/app.js');
}
const website = fs.readFileSync(websitePath, 'utf8').replace(/^\uFEFF/, '');
const marker = '// Initialize the app';
if (website.split(marker).length !== 2) throw Error('Unexpected website initialization marker.');
const integrated = fs.readFileSync(path.join(__dirname, 'admin-sync.js'), 'utf8') + '\n' + website.replace(marker, fs.readFileSync(path.join(__dirname, 'web-integration.js'), 'utf8') + '\n' + marker);
fs.writeFileSync(path.join(__dirname, 'app.cas.js'), integrated);
