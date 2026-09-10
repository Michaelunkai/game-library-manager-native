import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { createRequire } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { spawn } from 'node:child_process';

// Uses the installed official bundler directly. No Netlify CLI command, account
// configuration, credentials, site lookup, login, or deployment is invoked.
const here = path.dirname(fileURLToPath(import.meta.url));
const evidence = path.resolve(here, '../evidence');
const receiptPath = path.join(evidence, 'deployment-bundle.json');
const hash = value => crypto.createHash('sha256').update(value).digest('hex');
const requireCondition = (condition, message) => { if (!condition) throw new Error(message); };
const readJson = file => JSON.parse(fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, ''));
const cliPackage = path.join(process.env.APPDATA, 'npm/node_modules/netlify-cli/package.json');
const requireCli = createRequire(cliPackage);
const cli = readJson(cliPackage);
const bundlerPath = requireCli.resolve('@netlify/zip-it-and-ship-it');
const bundlerPackage = path.resolve(path.dirname(bundlerPath), '../package.json');
const bundler = readJson(bundlerPackage);
const toml = requireCli('toml');
const yauzl = requireCli('yauzl');
const stage = readJson(path.join(evidence, 'deployment-candidate.json'));
const stamp = new Date().toISOString().replace(/[:.]/g, '-');
const resume = process.argv[2];
requireCondition(!resume || /^deployment-bundle-[0-9TZ.-]+$/.test(resume), 'Resume must name an existing bundle fixture in the evidence directory.');
const fixture = path.join(evidence, resume || 'deployment-bundle-' + stamp);
const source = path.join(fixture, 'source');
const output = path.join(fixture, 'functions');
const proof = {
  at: new Date().toISOString(), passed: false, productionDeployed: false,
  site: stage.site, stagedSource: stage.stagedSource, fixture,
  toolchain: { cli: { version: cli.version, package: cliPackage }, bundler: { name: bundler.name, version: bundler.version, package: bundlerPackage }, node: process.version },
  checks: [], limitations: [
    'This is an actual local Netlify function ZIP build using the installed official bundler, not a hosted Netlify build.',
    'Live authentication, deployed function behavior, provider conditional writes, and production rollback deployment ID remain unverified.'
  ]
};
function check(name, condition, detail) {
  requireCondition(condition, name);
  proof.checks.push({ name, passed: true, ...(detail === undefined ? {} : { detail }) });
}
async function entries(zipPath) {
  return new Promise((resolve, reject) => {
    yauzl.open(zipPath, { lazyEntries: true }, (error, zip) => {
      if (error) { reject(error); return; }
      const result = new Map();
      zip.on('error', reject);
      zip.on('entry', entry => {
        if (entry.fileName.endsWith('/')) { zip.readEntry(); return; }
        requireCondition(!entry.fileName.split('/').includes('..'), 'Unsafe ZIP path.');
        zip.openReadStream(entry, (streamError, stream) => {
          if (streamError) { reject(streamError); return; }
          const chunks = [];
          stream.on('data', chunk => chunks.push(chunk));
          stream.on('error', reject);
          stream.on('end', () => { result.set(entry.fileName, Buffer.concat(chunks)); zip.readEntry(); });
        });
      });
      zip.on('end', () => resolve(result));
      zip.readEntry();
    });
  });
}
try {
  check('Installed CLI pins the exact local official bundler', cli.dependencies['@netlify/zip-it-and-ship-it'] === bundler.version);
  requireCondition(resume ? fs.existsSync(fixture) : !fs.existsSync(fixture), 'The isolated bundle fixture does not match the requested new/resume operation.');
  fs.mkdirSync(source, { recursive: true });
  for (const file of stage.files) {
    const from = path.join(stage.stagedSource, file.path);
    const to = path.join(source, file.path);
    requireCondition(hash(fs.readFileSync(from)) === file.sha256, 'Staged source differs from review: ' + file.path);
    fs.mkdirSync(path.dirname(to), { recursive: true });
    if (!fs.existsSync(to)) fs.copyFileSync(from, to, fs.constants.COPYFILE_EXCL);
  }
  check('All staged candidate source files copied without mutation', stage.files.every(file => hash(fs.readFileSync(path.join(source, file.path))) === file.sha256), stage.files.length);
  const npmCli = path.join(path.dirname(process.execPath), 'node_modules/npm/bin/npm-cli.js');
  requireCondition(fs.existsSync(npmCli), 'The installed npm CLI is unavailable.');
  const emptyNpmrc = path.join(fixture, 'empty.npmrc');
  const emptyGlobalNpmrc = path.join(fixture, 'empty-global.npmrc');
  fs.writeFileSync(emptyNpmrc, 'registry=https://registry.npmjs.org/\n');
  fs.writeFileSync(emptyGlobalNpmrc, '');
  const installLog = path.join(fixture, 'npm-ci-' + stamp + '.log');
  const lockBefore = hash(fs.readFileSync(path.join(source, 'package-lock.json')));
  const installExit = await new Promise((resolve, reject) => {
    const log = fs.openSync(installLog, 'wx');
    const child = spawn(process.execPath, [npmCli, 'ci', '--ignore-scripts', '--no-audit', '--no-fund'], {
      cwd: source, windowsHide: true,
      env: { ...process.env, npm_config_userconfig: emptyNpmrc, npm_config_globalconfig: emptyGlobalNpmrc, npm_config_registry: 'https://registry.npmjs.org/' },
      stdio: ['ignore', log, log]
    });
    child.on('error', error => { fs.closeSync(log); reject(error); });
    child.on('exit', code => { fs.closeSync(log); resolve(code); });
  });
  check('Isolated npm ci completed with lifecycle scripts disabled', installExit === 0, { exitCode: installExit, log: installLog });
  check('Explicit candidate package lock preserved after npm ci', hash(fs.readFileSync(path.join(source, 'package-lock.json'))) === lockBefore);
  const sdk = readJson(path.join(source, 'node_modules/@netlify/blobs/package.json'));
  check('Actual installed Blobs SDK matches reviewed 10.7.13 lock', sdk.version === '10.7.13', sdk.version);
  const config = toml.parse(fs.readFileSync(path.join(source, 'netlify.toml'), 'utf8'));
  check('Reviewed deployment targets the current Node 24 runtime', config.build.environment.NODE_VERSION === '24');
  proof.targetNodeVersion = config.build.environment.NODE_VERSION;
  const includedFiles = config.functions.included_files;
  const { zipFunctions } = await import(pathToFileURL(bundlerPath).href);
  const originalCwd = process.cwd();
  let archives;
  try {
    process.chdir(source);
    archives = await zipFunctions(path.join(source, config.build.functions), output, {
      archiveFormat: 'zip', basePath: source,
      config: { '*': { includedFiles, includedFilesBasePath: source, nodeVersion: config.build.environment.NODE_VERSION } },
      manifest: path.join(fixture, 'netlify-functions-manifest.json')
    });
  } finally { process.chdir(originalCwd); }
  const required = ['admin-config', 'docker-tags', 'game-metadata', 'image-proxy'];
  check('Official bundler produced exactly four required function ZIPs', archives.length === 4 && required.every(name => archives.some(item => item.name === name)));
  const bundleManifest = readJson(path.join(fixture, 'netlify-functions-manifest.json'));
  check('Every function manifest explicitly targets nodejs24.x', bundleManifest.functions.length === 4 && bundleManifest.functions.every(item => item.runtimeVersion === 'nodejs24.x'));
  proof.runtimeVersions = Object.fromEntries(bundleManifest.functions.map(item => [item.name, item.runtimeVersion]));
  proof.archives = [];
  for (const archive of archives) {
    const content = await entries(archive.path);
    const names = [...content.keys()];
    const bundled = relative => {
      const matches = names.filter(name => name === relative || name.endsWith('/' + relative));
      requireCondition(matches.length === 1, archive.name + ': expected one bundled ' + relative);
      return content.get(matches[0]);
    };
    const fallbacks = ['public/data/admin-config.json', 'public/data/games.json'];
    check(archive.name + ': declared public fallback data included byte-for-byte', fallbacks.every(file => hash(bundled(file)) === hash(fs.readFileSync(path.join(source, file)))));
    if (archive.name === 'admin-config') {
      const bundledSdk = JSON.parse(bundled('node_modules/@netlify/blobs/package.json').toString('utf8'));
      check('Admin function ZIP contains actual SDK 10.7.13 metadata and runtime', bundledSdk.version === '10.7.13' && names.some(name => name.includes('node_modules/@netlify/blobs/dist/') && /\.(cjs|mjs|js)$/.test(name)), bundledSdk.version);
      check('Admin function ZIP contains the reviewed safe-fetch guard', hash(bundled('lib/netlify-safe-fetch.js')) === hash(fs.readFileSync(path.join(source, 'lib/netlify-safe-fetch.js'))));
    }
    proof.archives.push({ name: archive.name, path: archive.path, bytes: fs.statSync(archive.path).size, sha256: hash(fs.readFileSync(archive.path)), entries: names.length, bundler: archive.bundler, runtime: archive.runtime, nodeVersion: archive.nodeVersion, fallbacks });
  }
  check('Original staged source and its pinned lock remain unchanged', stage.files.every(file => hash(fs.readFileSync(path.join(stage.stagedSource, file.path))) === file.sha256));
  proof.passed = true;
} catch (error) {
  proof.error = { name: error.name, message: error.message };
  process.exitCode = 1;
} finally {
  proof.finishedAt = new Date().toISOString();
  fs.mkdirSync(fixture, { recursive: true });
  fs.writeFileSync(path.join(fixture, 'verification.json'), JSON.stringify(proof, null, 2) + '\n');
  fs.writeFileSync(receiptPath, JSON.stringify(proof, null, 2) + '\n');
  console.log(JSON.stringify({ passed: proof.passed, checks: proof.checks.length, receipt: receiptPath, fixture, archives: proof.archives?.map(({ name, bytes, sha256, entries, bundler }) => ({ name, bytes, sha256, entries, bundler })), error: proof.error }));
}
