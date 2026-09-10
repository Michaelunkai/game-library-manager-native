'use strict';

// Local, allowlisted source staging only. This never authenticates or deploys.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { spawnSync } = require('node:child_process');
const root = path.resolve(__dirname, '../..');
const evidence = path.resolve(__dirname, '../evidence');
const receipt = JSON.parse(fs.readFileSync(path.join(evidence, 'maintained-source.json'), 'utf8'));
const hash = data => crypto.createHash('sha256').update(data).digest('hex');
const readJson = file => JSON.parse(fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, ''));
const requireCondition = (condition, message) => { if (!condition) throw new Error(message); };
const within = (parent, relative) => {
  requireCondition(typeof relative === 'string' && relative.length > 0 && !path.isAbsolute(relative), 'Expected a relative source path.');
  const resolved = path.resolve(parent, relative);
  requireCondition(resolved.startsWith(parent + path.sep), 'Path escapes the staged source.');
  return resolved;
};
function regularFile(file) {
  const stat = fs.lstatSync(file);
  requireCondition(stat.isFile() && !stat.isSymbolicLink(), 'Only regular source files may be staged: ' + path.relative(root, file));
  return stat;
}
function checkPin(folder) {
  const pkg = readJson(path.join(folder, 'package.json'));
  const lock = readJson(path.join(folder, 'package-lock.json'));
  const entry = lock.packages?.['node_modules/@netlify/blobs'];
  requireCondition(pkg.dependencies?.['@netlify/blobs'] === '10.7.13', 'Manifest must pin @netlify/blobs 10.7.13.');
  requireCondition(lock.packages?.['']?.dependencies?.['@netlify/blobs'] === '10.7.13', 'The lock root does not match the manifest pin.');
  requireCondition(entry?.version === '10.7.13', 'The resolved SDK version differs from the reviewed version.');
  requireCondition(entry.resolved === 'https://registry.npmjs.org/@netlify/blobs/-/blobs-10.7.13.tgz' && /^sha512-/.test(entry.integrity || ''), 'The SDK lock entry must retain its registry URL and integrity.');
  return { package: '@netlify/blobs', version: entry.version, lockfileVersion: lock.lockfileVersion, integrity: entry.integrity };
}
const sdk = checkPin(root);
const reviewed = receipt.files.map(file => {
  const target = within(root, file.path);
  regularFile(target);
  const current = hash(fs.readFileSync(target));
  requireCondition(current === file.afterSha256, 'Reviewed source has changed; update its review before staging: ' + file.path);
  return { path: file.path, sha256: current };
});

// Explicit package-lock inclusion is intentional: the project's .gitignore excludes it.
const files = [
  'package.json', 'package-lock.json', 'netlify.toml',
  'lib/netlify-safe-fetch.js',
  'netlify/functions/admin-config.js', 'netlify/functions/docker-tags.js',
  'netlify/functions/game-metadata.js', 'netlify/functions/image-proxy.js',
  'public/index.html', 'public/app.js', 'public/styles.css', 'public/manifest.json', 'public/sw.js',
  'public/data/admin-config.json', 'public/data/dates-added.json', 'public/data/games_latest.json',
  'public/data/games.json', 'public/data/image-sizes.json', 'public/data/tabs.json', 'public/data/times.json'
];
const imageRoot = path.join(root, 'public/images');
requireCondition(!fs.lstatSync(imageRoot).isSymbolicLink(), 'The public images directory must be a real directory.');
function images(folder) {
  for (const entry of fs.readdirSync(folder, { withFileTypes: true })) {
    const file = path.join(folder, entry.name);
    requireCondition(!entry.isSymbolicLink(), 'Image links are excluded: ' + entry.name);
    if (entry.isDirectory()) images(file);
    else {
      requireCondition(entry.isFile() && /\.(png|jpe?g|svg|webp|gif|ico|avif)$/i.test(entry.name), 'Unexpected file in the served image directory: ' + entry.name);
      files.push(path.relative(root, file).split(path.sep).join('/'));
    }
  }
}
images(imageRoot);
requireCondition(new Set(files).size === files.length, 'Duplicate staged source path.');
files.sort();
for (const file of files) regularFile(within(root, file));
// Fail closed for private keys or high-confidence provider tokens accidentally added to source.
// Existing browser-visible application auth conventions are not redesigned by this packager.
const sensitive = /-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|\bgh[pousr]_[A-Za-z0-9]{30,}\b|\bgithub_pat_[A-Za-z0-9_]{40,}\b|\bglpat-[A-Za-z0-9_-]{20,}\b/;
for (const file of files.filter(f => !f.startsWith('public/images/'))) {
  requireCondition(!sensitive.test(fs.readFileSync(within(root, file), 'utf8')), 'A provider credential/private key pattern was found; this file cannot be staged: ' + file);
}
const stamp = new Date().toISOString().replace(/[:.]/g, '-');
const target = path.join(evidence, 'deployment-candidate-' + stamp);
const source = path.join(target, 'source');
requireCondition(!fs.existsSync(target), 'The staging directory already exists.');
const totalBytes = files.reduce((sum, file) => sum + fs.statSync(within(root, file)).size, 0);
const space = fs.statfsSync(evidence);
requireCondition(space.bavail * space.bsize > totalBytes + 64 * 1024 * 1024, 'Insufficient free space for an isolated deployment candidate.');
fs.mkdirSync(source, { recursive: true });
const manifestFiles = [];
for (const file of files) {
  const from = within(root, file), to = within(source, file);
  fs.mkdirSync(path.dirname(to), { recursive: true });
  fs.copyFileSync(from, to, fs.constants.COPYFILE_EXCL);
  const sha256 = hash(fs.readFileSync(from));
  requireCondition(hash(fs.readFileSync(to)) === sha256, 'Staged checksum differs from source: ' + file);
  manifestFiles.push({ path: file, bytes: fs.statSync(to).size, sha256 });
}
for (const file of reviewed) {
  requireCondition(manifestFiles.find(entry => entry.path === file.path)?.sha256 === file.sha256, 'Reviewed source changed while staging: ' + file.path);
}
checkPin(source);
const syntax = [];
for (const file of files.filter(f => f.endsWith('.js'))) {
  const result = spawnSync(process.execPath, ['--check', within(source, file)], { encoding: 'utf8', windowsHide: true });
  requireCondition(result.status === 0, 'JavaScript syntax check failed: ' + file);
  syntax.push(file);
}
const config = fs.readFileSync(path.join(source, 'netlify.toml'), 'utf8');
requireCondition(/publish\s*=\s*"public"/.test(config) && /functions\s*=\s*"netlify\/functions"/.test(config), 'Unexpected Netlify deployment directories.');
const routes = ['admin-config', 'docker-tags', 'game-metadata', 'image-proxy'];
for (const route of routes) requireCondition(config.includes('from = "/api/' + route + '"') && config.includes('to = "/.netlify/functions/' + route + '"'), 'A required function route is missing: ' + route);
const declaredIncludedFiles = [...config.matchAll(/included_files\s*=\s*\[([^\]]+)\]/g)]
  .flatMap(match => [...match[1].matchAll(/"([^"]+)"/g)].map(entry => entry[1]));
const missingDeclaredOptionalFiles = declaredIncludedFiles.filter(file => !fs.existsSync(within(source, file)));
requireCondition(missingDeclaredOptionalFiles.every(file => file === 'data/admin-config.json'), 'A required bundled function data file is missing.');
for (const file of ['public/data/admin-config.json', 'public/data/games.json']) {
  requireCondition(fs.existsSync(within(source, file)), 'A function fallback data document is missing: ' + file);
  readJson(within(source, file));
}
const result = {
  at: new Date().toISOString(), passed: true, productionDeployed: false,
  site: { name: 'game-library-michaelunkai', id: 'c8ccb88c-0b80-486f-940b-e89d9acefe99', url: 'https://game-library-michaelunkai.netlify.app' },
  sourceRoot: root, stagedSource: source, upstreamCommit: receipt.upstreamCommit,
  sdk, lockExplicitlyIncluded: true, packageLockSha256: manifestFiles.find(file => file.path === 'package-lock.json').sha256,
  reviewedSourceFiles: reviewed, fileCount: manifestFiles.length, imageCount: files.filter(file => file.startsWith('public/images/')).length,
  totalBytes, manifestSha256: hash(JSON.stringify(manifestFiles)), syntaxChecks: syntax, routes,
  excluded: ['.git', '.netlify', '.env*', 'node_modules', 'native', 'private source backups', 'evidence', 'credentials and CLI state'],
  missingDeclaredOptionalFiles,
  limitations: [
    'Source staging and JavaScript syntax were verified locally; this is not a Netlify function-bundler or live-provider test.',
    'The existing optional data/admin-config.json fallback is absent, as in the maintained checkout; its public/data/admin-config.json fallback is included.',
    'Existing application authentication behavior is preserved; staging does not replace its browser-visible application auth conventions.',
    'Authenticated same-site draft build, previous production deployment rollback ID, and live native/browser acceptance remain required.'
  ],
  files: manifestFiles
};
fs.writeFileSync(path.join(target, 'manifest.json'), JSON.stringify(result, null, 2) + '\n', { flag: 'wx' });
fs.writeFileSync(path.join(evidence, 'deployment-candidate.json'), JSON.stringify(result, null, 2) + '\n');
console.log(JSON.stringify({ passed: result.passed, stagedSource: source, manifest: path.join(target, 'manifest.json'), fileCount: result.fileCount, imageCount: result.imageCount, totalBytes, sdk: sdk.version, lockExplicitlyIncluded: true, packageLockSha256: result.packageLockSha256, syntaxChecks: syntax.length, missingDeclaredOptionalFiles }));
