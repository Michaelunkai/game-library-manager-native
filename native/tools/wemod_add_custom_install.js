const fs = require("fs");
const path = require("path");
const { execFileSync } = require("child_process");
const bundledRuntime = path.join(__dirname, "wand-runtime");
if (fs.existsSync(path.join(bundledRuntime, "classic-level"))) {
  process.env.NODE_PATH = [bundledRuntime, process.env.NODE_PATH || ""].filter(Boolean).join(path.delimiter);
  require("module")._initPaths();
}
const classicLevelPath = fs.existsSync(path.join(bundledRuntime, "classic-level"))
  ? path.join(bundledRuntime, "classic-level")
  : path.join(__dirname, "..", "node_modules", "classic-level");
const { ClassicLevel } = require(classicLevelPath);

const HOME = process.env.USERPROFILE || process.env.HOME;
const ROOTS = [
  path.join(HOME, "AppData", "Roaming", "wemod", "Local Storage", "leveldb"),
  path.join(HOME, "AppData", "Roaming", "Wand", "Local Storage", "leveldb"),
];
const KEY_NAME = "infinity:globalStore";

function usage() {
  console.error("Usage: node wemod_add_custom_install.js <gameId> <gameExePath>");
  process.exit(2);
}

function decodeValue(buffer) {
  if (buffer.length >= 2 && buffer[0] <= 4 && buffer[1] !== 0) {
    return { text: buffer.subarray(1).toString("utf16le").replace(/\u0000+$/g, ""), endian: `typed-${buffer[0]}-le` };
  }
  return { text: buffer.toString("utf16le").replace(/\u0000+$/g, ""), endian: "le" };
}

function encodeValue(text, endian) {
  const utf16le = Buffer.from(text, "utf16le");
  const typedMatch = /^typed-(\d+)-le$/.exec(endian);
  if (typedMatch) {
    return Buffer.concat([Buffer.from([Number(typedMatch[1])]), utf16le]);
  }
  return utf16le;
}

function sanitizeJson(text) {
  return text.replace(/[\u0000-\u0008\u000b\u000c\u000e-\u001f]/g, (char) => {
    return `\\u${char.charCodeAt(0).toString(16).padStart(4, "0")}`;
  }).replace(/\t/g, "\\t").replace(/\r/g, "\\r").replace(/\n/g, "\\n");
}

function stopWandProcesses() {
  try {
    execFileSync("powershell.exe", [
      "-NoProfile",
      "-ExecutionPolicy",
      "Bypass",
      "-Command",
      "Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -in @('Wand','WeMod','WandAuxiliaryService') -or $_.Path -like '*\\Wand\\*' -or $_.Path -like '*\\WeMod\\*' } | Stop-Process -Force -ErrorAction SilentlyContinue",
    ], { stdio: "ignore" });
  } catch {
    // Best effort. The DB write below will still report a real failure if locked.
  }
}

function isLockedDatabaseError(error) {
  const message = String(error && (error.stack || error.message) || error).toLowerCase();
  return message.includes("lock") || message.includes("database failed to open") || message.includes("failed to open");
}

async function openDb(root) {
  for (let attempt = 0; attempt < 4; attempt += 1) {
    const db = new ClassicLevel(root, { keyEncoding: "buffer", valueEncoding: "buffer" });
    try {
      await db.open();
      return db;
    } catch (error) {
      try {
        await db.close();
      } catch {
        // Ignore close errors from a failed open.
      }
      if (!isLockedDatabaseError(error) || attempt === 3) {
        throw error;
      }
      stopWandProcesses();
      await new Promise((resolve) => setTimeout(resolve, 750 + attempt * 750));
    }
  }
  throw new Error(`Database failed to open after retries: ${root}`);
}

function updateState(state, gameId, exePath) {
  const normalizedPath = exePath.toLowerCase();
  const sku = `${gameId}_${normalizedPath}`;
  const correlationId = `custom:${sku}`;
  state.installedApps = state.installedApps || {};
  state.installedGameVersions = state.installedGameVersions || {};

  // Preserve other custom installs for the same WeMod game id. The user's E: library
  // can contain multiple executables that resolve to one title/game id, and zero-exception
  // coverage means adding one must not silently delete another.
  const existingVersions = Array.isArray(state.installedGameVersions[gameId])
    ? state.installedGameVersions[gameId]
    : [];
  const retainedVersions = existingVersions.filter((entry) => {
    return !(entry && entry.correlationId === correlationId);
  });

  state.installedApps[correlationId] = {
    platform: "custom",
    sku,
    location: exePath,
  };
  // Wand resolves the preferred installation by scanning this array and stopping
  // at the first custom entry. Put the exact path first so a stale bootstrap or
  // older custom install can never win the launch decision.
  state.installedGameVersions[gameId] = [{
    gameId,
    correlationId,
    version: null,
    modifiedAt: null,
    createdAt: null,
  }, ...retainedVersions];
  const preferred = state.installedGameVersions[gameId][0]?.correlationId === correlationId;
  return { sku, correlationId, preferred };
}

function removeState(state, gameId, exePath) {
  const normalizedPath = exePath.toLowerCase();
  const sku = `${gameId}_${normalizedPath}`;
  const correlationId = `custom:${sku}`;
  let removed = false;
  if (state.installedApps && Object.prototype.hasOwnProperty.call(state.installedApps, correlationId)) {
    delete state.installedApps[correlationId];
    removed = true;
  }
  const versions = Array.isArray(state.installedGameVersions && state.installedGameVersions[gameId])
    ? state.installedGameVersions[gameId]
    : [];
  const kept = versions.filter((entry) => !(entry && entry.correlationId === correlationId));
  if (kept.length !== versions.length) {
    removed = true;
  }
  if (state.installedGameVersions) {
    if (kept.length) {
      state.installedGameVersions[gameId] = kept;
    } else {
      delete state.installedGameVersions[gameId];
    }
  }
  return { sku, correlationId, removed };
}

function customGameId(correlationId, app) {
  const location = typeof app?.location === "string" ? app.location.toLowerCase() : "";
  const prefix = "custom:";
  const suffix = location ? `_${location}` : "";
  if (correlationId.startsWith(prefix) && suffix && correlationId.endsWith(suffix)) {
    return correlationId.slice(prefix.length, correlationId.length - suffix.length);
  }
  return "";
}

function removeCorrelationReferences(value, correlationId) {
  if (Array.isArray(value)) {
    for (let index = value.length - 1; index >= 0; index -= 1) {
      if (value[index]?.correlationId === correlationId) {
        value.splice(index, 1);
      } else {
        removeCorrelationReferences(value[index], correlationId);
      }
    }
    return;
  }
  if (value && typeof value === "object") {
    for (const [key, child] of Object.entries(value)) {
      if (child?.correlationId === correlationId) {
        delete value[key];
      } else {
        removeCorrelationReferences(child, correlationId);
      }
    }
  }
}

function hasCorrelationReference(value, correlationId) {
  if (Array.isArray(value)) {
    return value.some((item) => hasCorrelationReference(item, correlationId));
  }
  if (value && typeof value === "object") {
    if (value.correlationId === correlationId) {
      return true;
    }
    return Object.values(value).some((item) => hasCorrelationReference(item, correlationId));
  }
  return false;
}

function reconcileState(state, approved) {
  state.installedApps = state.installedApps || {};
  state.installedGameVersions = state.installedGameVersions || {};
  const removed = [];
  for (const [correlationId, app] of Object.entries(state.installedApps)) {
    if (!correlationId.startsWith("custom:") || app?.platform !== "custom") {
      continue;
    }
    const exePath = typeof app.location === "string" ? app.location : "";
    if (!exePath.toLowerCase().startsWith("e:\\")) {
      continue;
    }
    const gameId = customGameId(correlationId, app);
    const key = `${gameId}\u0000${exePath.toLowerCase()}`;
    if (approved.has(key)) {
      continue;
    }
    delete state.installedApps[correlationId];
    removeCorrelationReferences(state.installedGameVersions, correlationId);
    removed.push({ correlationId, gameId, exePath });
  }
  return { removed };
}

async function updateRoot(root, gameId, exePath, removeOnly) {
  if (!fs.existsSync(root)) {
    return { root, skipped: true, reason: "missing-leveldb" };
  }
  const db = await openDb(root);
  try {
    let targetKey = null;
    let targetValue = null;
    for await (const [key, value] of db.iterator()) {
      if (key.toString("utf8").includes(KEY_NAME)) {
        targetKey = key;
        targetValue = value;
      }
    }
    if (!targetKey || !targetValue) {
      return { root, skipped: true, reason: "missing-global-store" };
    }
    const decoded = decodeValue(targetValue);
    const state = JSON.parse(sanitizeJson(decoded.text));
    const result = removeOnly ? removeState(state, gameId, exePath) : updateState(state, gameId, exePath);
    await db.put(targetKey, encodeValue(JSON.stringify(state), decoded.endian));
    return { root, updated: true, ...result };
  } finally {
    await db.close();
  }
}

async function reconcileRoot(root, approved) {
  if (!fs.existsSync(root)) {
    return { root, skipped: true, reason: "missing-leveldb" };
  }
  const db = await openDb(root);
  try {
    let targetKey = null;
    let targetValue = null;
    for await (const [key, value] of db.iterator()) {
      if (key.toString("utf8").includes(KEY_NAME)) {
        targetKey = key;
        targetValue = value;
      }
    }
    if (!targetKey || !targetValue) {
      return { root, skipped: true, reason: "missing-global-store" };
    }
    const decoded = decodeValue(targetValue);
    const state = JSON.parse(sanitizeJson(decoded.text));
    const result = reconcileState(state, approved);
    await db.put(targetKey, encodeValue(JSON.stringify(state), decoded.endian));
    return { root, updated: true, ...result };
  } finally {
    await db.close();
  }
}

async function checkRoot(root, gameId, exePath) {
  if (!fs.existsSync(root)) {
    return { root, skipped: true, reason: "missing-leveldb" };
  }
  const db = await openDb(root);
  try {
    let targetValue = null;
    for await (const [key, value] of db.iterator()) {
      if (key.toString("utf8").includes(KEY_NAME)) {
        targetValue = value;
      }
    }
    if (!targetValue) {
      return { root, skipped: true, reason: "missing-global-store" };
    }
    const decoded = decodeValue(targetValue);
    const state = JSON.parse(sanitizeJson(decoded.text));
    const normalizedPath = exePath.toLowerCase();
    const sku = `${gameId}_${normalizedPath}`;
    const correlationId = `custom:${sku}`;
    const app = state.installedApps && state.installedApps[correlationId];
    const versions = Array.isArray(state.installedGameVersions && state.installedGameVersions[gameId])
      ? state.installedGameVersions[gameId]
      : [];
    const firstCustom = versions.find((entry) => {
      return state.installedApps?.[entry?.correlationId]?.platform === "custom";
    });
    const preferred = firstCustom?.correlationId === correlationId;
    const ok = !!app
      && app.platform === "custom"
      && app.sku === sku
      && String(app.location || "").toLowerCase() === normalizedPath
      && hasCorrelationReference(state.installedGameVersions, correlationId)
      && preferred;
    return { root, checked: true, ok, sku, correlationId, preferred };
  } finally {
    await db.close();
  }
}

async function listRoot(root) {
  if (!fs.existsSync(root)) {
    return { root, checked: false, reason: "missing-leveldb", records: [] };
  }
  const db = await openDb(root);
  try {
    let targetValue = null;
    for await (const [key, value] of db.iterator()) {
      if (key.toString("utf8").includes(KEY_NAME)) {
        targetValue = value;
      }
    }
    if (!targetValue) {
      return { root, checked: false, reason: "missing-global-store", records: [] };
    }
    const decoded = decodeValue(targetValue);
    const state = JSON.parse(sanitizeJson(decoded.text));
    const records = [];
    for (const [correlationId, app] of Object.entries(state.installedApps || {})) {
      if (!correlationId.startsWith("custom:") || app?.platform !== "custom") {
        continue;
      }
      const gameId = customGameId(correlationId, app);
      records.push({
        correlationId,
        gameId,
        exePath: typeof app.location === "string" ? app.location : "",
        versionLinked: hasCorrelationReference(state.installedGameVersions, correlationId),
      });
    }
    return { root, checked: true, records };
  } finally {
    await db.close();
  }
}

async function main() {
  const mode = process.argv[2];
  const checkOnly = mode === "--check";
  const removeOnly = mode === "--remove";
  const listOnly = mode === "--list";
  const reconcileOnly = mode === "--reconcile";
  if (listOnly) {
    const results = [];
    for (const root of ROOTS) {
      results.push(await listRoot(root));
    }
    const ok = results.some((item) => item.checked);
    console.log(JSON.stringify({ ok, listOnly: true, results }, null, 2));
    if (!ok) {
      process.exitCode = 1;
    }
    return;
  }
  if (reconcileOnly) {
    const manifestPath = process.argv[3];
    if (!manifestPath || !fs.existsSync(manifestPath)) {
      throw new Error(`Reconciliation manifest does not exist: ${manifestPath || "(missing)"}`);
    }
    const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
    const entries = Array.isArray(manifest.entries) ? manifest.entries : [];
    const approved = new Set(entries.map((entry) => {
      return `${String(entry.gameId || "")}\u0000${String(entry.exePath || "").toLowerCase()}`;
    }));
    const results = [];
    for (const root of ROOTS) {
      results.push(await reconcileRoot(root, approved));
    }
    if (!results.some((item) => item.updated)) {
      throw new Error(`No Wand/WeMod global store was reconciled: ${JSON.stringify(results)}`);
    }
    console.log(JSON.stringify({ ok: true, reconcileOnly: true, approved: approved.size, results }, null, 2));
    return;
  }
  const offset = (checkOnly || removeOnly) ? 3 : 2;
  const gameId = process.argv[offset];
  const exePath = process.argv.slice(offset + 1).join(" ");
  if (!gameId || !exePath) usage();
  if (!removeOnly && (!fs.existsSync(exePath) || path.extname(exePath).toLowerCase() !== ".exe")) {
    throw new Error(`Game executable does not exist or is not an .exe: ${exePath}`);
  }
  const results = [];
  for (const root of ROOTS) {
    results.push(checkOnly ? await checkRoot(root, gameId, exePath) : await updateRoot(root, gameId, exePath, removeOnly));
  }
  if (checkOnly) {
    const checked = results.filter((item) => item.checked);
    if (!checked.length || !checked.every((item) => item.ok)) {
      console.error(JSON.stringify({ ok: false, gameId, exePath, results }, null, 2));
      process.exit(1);
    }
    console.log(JSON.stringify({ ok: true, checkOnly: true, gameId, exePath, results }, null, 2));
    return;
  }
  const updated = results.filter((item) => item.updated);
  if (!updated.length || !updated.every((item) => item.preferred)) {
    throw new Error(`No Wand/WeMod global store was updated: ${JSON.stringify(results)}`);
  }
  console.log(JSON.stringify({ ok: true, gameId, exePath, removeOnly, results }, null, 2));
}

main().catch((error) => {
  const message = error && (error.stack || error.message) || String(error);
  console.error(message.replace(/\r?\n\s+at .*/g, ""));
  process.exit(1);
});
