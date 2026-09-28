const fs = require('fs');
const path = require('path');
const os = require('os');
const runtime = path.join(__dirname, 'wand-runtime');
process.env.NODE_PATH = [runtime, process.env.NODE_PATH || ''].filter(Boolean).join(path.delimiter);
require('module')._initPaths();

const genericInstallFolders = new Set(['bin', 'binaries', 'game', 'games', 'release', 'shipping', 'win32', 'win64', 'x64', 'x86']);

function isWithin(root, candidate) {
  const relative = path.relative(path.resolve(root), path.resolve(candidate));
  return relative !== '..' && !relative.startsWith(`..${path.sep}`) && !path.isAbsolute(relative);
}

function isRegistrationFile(candidate) {
  if (!/\.(exe|bat|cmd)$/i.test(candidate)) return false;
  try { return fs.statSync(candidate).isFile(); }
  catch (error) { return error.code === 'ENOENT' || error.code === 'ENOTDIR'; }
}

function folderFromVersionPath(executable, versionPath) {
  if (typeof versionPath !== 'string' || !versionPath.trim() || path.isAbsolute(versionPath)) return '';
  const executableParts = path.resolve(executable).split(path.sep).filter(Boolean);
  const versionParts = path.normalize(versionPath).split(path.sep).filter(part => part && part !== '.');
  if (!versionParts.length || executableParts.length <= versionParts.length) return '';
  const suffix = executableParts.slice(-versionParts.length);
  const sameSegment = process.platform === 'win32'
    ? (left, right) => left.toLowerCase() === right.toLowerCase()
    : (left, right) => left === right;
  if (!suffix.every((part, index) => sameSegment(part, versionParts[index]))) return '';
  const root = executableParts.slice(0, -versionParts.length).join(path.sep);
  const folder = path.basename(root);
  return folder && !genericInstallFolders.has(folder.toLowerCase()) ? folder : '';
}

function registrationFolder(location, executable, versionPath, gameId) {
  const fromVersionPath = folderFromVersionPath(executable, versionPath);
  if (fromVersionPath) return fromVersionPath;

  const locationIsExecutable = /\.(exe|bat|cmd)$/i.test(location);
  const base = locationIsExecutable ? path.dirname(executable) : path.resolve(location);
  if (!locationIsExecutable) return path.basename(base) || gameId;

  let current = base;
  while (current && path.dirname(current) !== current) {
    const folder = path.basename(current);
    if (folder && !genericInstallFolders.has(folder.toLowerCase())) return folder;
    current = path.dirname(current);
  }
  return path.basename(base) || gameId;
}

function registrations(state) {
  if (!state.installedGameVersions || !state.installedApps || !state.catalog?.games || !state.catalog?.titles)
    throw new Error('Wand library schema is unavailable');
  const rows = [];
  for (const [gameId, versions] of Object.entries(state.installedGameVersions)) {
    if (!Array.isArray(versions) || !versions.length) continue;
    const game = state.catalog.games[gameId];
    const title = state.catalog.titles[game?.titleId];
    const titleId = game && (typeof game.titleId === 'string' || Number.isSafeInteger(game.titleId)) ? String(game.titleId) : '';
    if (!game || !title || !gameId.trim() || gameId !== gameId.trim() || !titleId.trim()
        || typeof title.name !== 'string' || !title.name.trim())
      throw new Error('A registered Wand game is missing catalog identity');
    const versionPath = typeof game.versionPath === 'string' ? game.versionPath : '';
    const candidates = versions.map(v => state.installedApps[v.correlationId]).filter(Boolean).map(app => {
      const location = app.location;
      if (typeof location !== 'string' || !path.isAbsolute(location)) return null;
      const locationPath = path.resolve(location);
      let executable;
      if (/\.(exe|bat|cmd)$/i.test(locationPath)) executable = locationPath;
      else {
        if (!versionPath.trim() || path.isAbsolute(versionPath)) return null;
        executable = path.resolve(locationPath, versionPath);
        if (!isWithin(locationPath, executable)) return null;
      }
      if (!isRegistrationFile(executable)) return null;
      return { folder: registrationFolder(locationPath, executable, versionPath, gameId), titleId, gameId,
        name: title.name, path: executable };
    }).filter(Boolean);
    if (!candidates.length) throw new Error('A registered Wand game has no safe installation path');
    rows.push(candidates.find(row => {
      try { return fs.statSync(row.path).isFile(); }
      catch (error) { return error.code !== 'ENOENT' && error.code !== 'ENOTDIR'; }
    }) || candidates[0]);
  }
  return rows;
}

async function readLibrary(root) {
  const { ClassicLevel } = require(path.join(runtime, 'classic-level'));
  const scratch = fs.mkdtempSync(path.join(os.tmpdir(), 'GameLibrary-Wand-'));
  try {
    const snapshot = path.join(scratch, 'leveldb');
    fs.cpSync(root, snapshot, { recursive: true, filter: source => path.basename(source) !== 'LOCK' });
    const db = new ClassicLevel(snapshot, { keyEncoding: 'buffer', valueEncoding: 'buffer', createIfMissing: false });
    await db.open();
    try {
      for await (const [key, value] of db.iterator()) {
        if (!key.toString('utf8').endsWith('infinity:globalStore')) continue;
        const text = (value[0] <= 4 ? value.subarray(1) : value).toString('utf16le').replace(/\u0000+$/g, '');
        return registrations(JSON.parse(text));
      }
      throw new Error('Wand library was not found');
    } finally { await db.close(); }
  } finally {
    const resolved = fs.realpathSync(scratch);
    if (path.dirname(resolved).toLowerCase() !== fs.realpathSync(os.tmpdir()).toLowerCase()
        || !path.basename(resolved).startsWith('GameLibrary-Wand-')) throw new Error('Unsafe snapshot cleanup path');
    fs.rmSync(resolved, { recursive: true, force: true });
  }
}
module.exports = { registrations, readLibrary };
if (require.main === module) readLibrary(process.argv[2]).then(rows => process.stdout.write(JSON.stringify(rows)))
  .catch(error => { console.error(error.message); process.exitCode = 1; });
