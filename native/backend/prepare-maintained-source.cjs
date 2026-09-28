// Reconciles the authoritative checkout with the inspected production repository.
// This does not push GitHub commits or deploy Netlify. Rollback originals stay private.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const root = path.resolve(__dirname, '../..');
const hash = data => crypto.createHash('sha256').update(data).digest('hex');
const headers = { 'User-Agent': 'GameLibrarySourceVerification' };
(async () => {
  const response = await fetch('https://api.github.com/repos/Michaelunkai/game-library-manager-web/commits/main', { headers });
  if (!response.ok) throw Error('Could not resolve maintained source revision: ' + response.status);
  const commit = (await response.json()).sha;
  const files = ['public/app.js', 'netlify.toml', 'package.json', 'netlify/functions/admin-config.js', 'netlify/functions/docker-tags.js', 'netlify/functions/game-metadata.js', 'netlify/functions/image-proxy.js'];
  const upstream = new Map();
  for (const name of files) {
    const r = await fetch('https://raw.githubusercontent.com/Michaelunkai/game-library-manager-web/' + commit + '/' + name);
    if (!r.ok) throw Error('Missing maintained source: ' + name + ' HTTP ' + r.status);
    upstream.set(name, await r.text());
  }
  const localApp = fs.readFileSync(path.join(root, 'public/app.js'), 'utf8').replace(/^\uFEFF/, '');
  if (localApp.replace(/\r\n/g, '\n') !== upstream.get('public/app.js').replace(/\r\n/g, '\n')) throw Error('Website changed from the inspected baseline. Reconcile changes before preparing again.');
  if (upstream.get('netlify/functions/admin-config.js') !== fs.readFileSync(path.join(__dirname, 'admin-config.production-reference.js'), 'utf8')) throw Error('Backend production reference drifted; review current source.');
  const next = new Map(upstream);
  next.set('public/app.js', fs.readFileSync(path.join(__dirname, 'app.cas.js'), 'utf8'));
  next.set('netlify/functions/admin-config.js', fs.readFileSync(path.join(__dirname, 'admin-config.js'), 'utf8').replace("require('./safe-fetch')", "require('../../lib/netlify-safe-fetch')"));
  next.set('lib/netlify-safe-fetch.js', fs.readFileSync(path.join(__dirname, 'safe-fetch.js'), 'utf8'));
  const pkg = JSON.parse(upstream.get('package.json')); pkg.dependencies['@netlify/blobs'] = '10.7.13';
  next.set('package.json', JSON.stringify(pkg, null, 2) + '\n');
  const backup = path.join(process.env.LOCALAPPDATA, 'GameLibraryManager', 'source-backups', new Date().toISOString().replace(/[:.]/g, '-'));
  fs.mkdirSync(backup, { recursive: true });
  const receipt = { at: new Date().toISOString(), sourceRoot: root, upstreamCommit: commit, backupRoot: backup, productionDeployed: false, files: [] };
  for (const [name, content] of next) {
    const destination = path.join(root, name), existed = fs.existsSync(destination);
    const before = existed ? fs.readFileSync(destination) : null;
    if (existed) { fs.mkdirSync(path.dirname(path.join(backup, name)), { recursive: true }); fs.copyFileSync(destination, path.join(backup, name)); }
    receipt.files.push({ path: name, existed, beforeSha256: before ? hash(before) : null, afterSha256: hash(content) });
  }
  // Receipt precedes mutations so an interrupted prepare remains reconcilable.
  fs.writeFileSync(path.join(backup, 'receipt.json'), JSON.stringify(receipt, null, 2));
  for (const [name, content] of next) { const destination = path.join(root, name); fs.mkdirSync(path.dirname(destination), { recursive: true }); fs.writeFileSync(destination, content); }
  fs.writeFileSync(path.join(__dirname, '../evidence/maintained-source.json'), JSON.stringify(receipt, null, 2));
  console.log(JSON.stringify({ prepared: true, commit, files: receipt.files.length, rollbackReceipt: path.join(backup, 'receipt.json'), productionDeployed: false }));
})().catch(error => { console.error(error.message); process.exitCode = 1; });
