'use strict';

const http = require('http');
const path = require('path');
const { inspectTrainerTraceDirectory } = require('./wand_tophat_evidence');

const CDP_LIST_URL = 'http://127.0.0.1:9222/json/list';
const TOTAL_TIMEOUT_MS = 1800;
const HTTP_TIMEOUT_MS = 500;
const SOCKET_STEP_TIMEOUT_MS = 750;
const MAX_TARGET_BYTES = 512 * 1024;
const MAX_PLAY_BUTTONS = 256;

// Read only the 12.58.0 Aurelia custom-element view models exposed by the
// page. This expression does not click, navigate, dispatch events, or mutate.
const READ_ONLY_EXPRESSION = `(() => {
  const elements = Array.from(document.querySelectorAll('sidebar-play-button'));
  if (elements.length > ${MAX_PLAY_BUTTONS}) {
    return { overflow: true, nodeCount: elements.length, nodes: [] };
  }

  const nodes = elements.map((element) => {
    const controller = element && element.au && element.au.controller;
    const viewModel = controller && controller.viewModel;
    if (!viewModel) return { vmPresent: false };

    const gameInfo = viewModel.gameInfo;
    const visibility = viewModel.trainerVisibility;
    const runningTrainer = visibility && visibility.runningTrainer;
    const trainerInfo = runningTrainer && runningTrainer.info;
    const activeTrainer = viewModel.trainerService && viewModel.trainerService.trainer;
    const process = activeTrainer && activeTrainer.process;
    return {
      vmPresent: true,
      gameInfoPresent: !!gameInfo && typeof gameInfo === 'object',
      gameInfoId: gameInfo && gameInfo.id,
      state: viewModel.state,
      trainerId: viewModel.trainerId,
      runningTrainerPresent: !!runningTrainer,
      trainerInfoPresent: !!trainerInfo && typeof trainerInfo === 'object',
      runningTrainerInfoId: trainerInfo && trainerInfo.id,
      runningTrainerGameId: trainerInfo && trainerInfo.gameId,
      trainerProcessId: process && process.id
    };
  });

  return { overflow: false, nodeCount: elements.length, nodes };
})()`;

function parsePositiveSafeInteger(raw, label) {
  if (typeof raw !== 'string' || !/^[1-9]\d*$/.test(raw)) {
    throw new Error(`${label} must be a positive decimal integer`);
  }
  const value = Number(raw);
  if (!Number.isSafeInteger(value) || String(value) !== raw) {
    throw new Error(`${label} must be a canonical safe integer`);
  }
  return value;
}

function parseExpectedIdentity(gameIdArg, processIdArg) {
  return {
    gameId: String(gameIdArg),
    numericGameId: parsePositiveSafeInteger(String(gameIdArg), 'gameId'),
    processId: parsePositiveSafeInteger(String(processIdArg), 'processId')
  };
}

function isRecord(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function parseGameInfoId(value) {
  if (Number.isSafeInteger(value) && value > 0) return { value, canonical: true };
  if (typeof value !== 'string' || !/^\d+$/.test(value)) return null;
  const numeric = Number(value);
  if (!Number.isSafeInteger(numeric) || numeric <= 0) return null;
  return { value: numeric, canonical: String(numeric) === value };
}

function isExpectedWandPageUrl(rawUrl) {
  if (typeof rawUrl !== 'string') return false;
  try {
    const url = new URL(rawUrl);
    if (url.protocol !== 'file:' || url.host !== '' || url.username || url.password || url.search) return false;
    const decodedPath = decodeURIComponent(url.pathname).replace(/\\/g, '/');
    return /\/Wand\/app-12\.58\.0\/resources\/app\.asar\/index\.html$/i.test(decodedPath);
  } catch {
    return false;
  }
}

function isExpectedWebSocketTarget(target) {
  if (!isRecord(target) || typeof target.id !== 'string' || !/^[A-Za-z0-9._-]+$/.test(target.id)
      || typeof target.webSocketDebuggerUrl !== 'string') return false;
  try {
    const url = new URL(target.webSocketDebuggerUrl);
    return url.protocol === 'ws:'
      && url.hostname === '127.0.0.1'
      && url.port === '9222'
      && url.username === ''
      && url.password === ''
      && url.search === ''
      && url.hash === ''
      && url.pathname === `/devtools/page/${target.id}`;
  } catch {
    return false;
  }
}

function selectWandTarget(targets) {
  if (!Array.isArray(targets)) throw new Error('CDP target list is not an array');
  for (const target of targets) {
    if (!isRecord(target) || typeof target.type !== 'string') {
      throw new Error('CDP target list contains a malformed entry');
    }
    if (target.type === 'page' && (typeof target.title !== 'string' || typeof target.url !== 'string')) {
      throw new Error('CDP page target is missing its title or URL');
    }
  }

  const wandPages = targets.filter((target) => target.type === 'page' && target.title === 'Wand');
  if (wandPages.length === 0) return { target: null, reason: 'matching-view-not-found' };
  if (wandPages.length !== 1) return { target: null, reason: 'ambiguous-wand-target' };

  const target = wandPages[0];
  if (!isExpectedWandPageUrl(target.url) || !isExpectedWebSocketTarget(target)) {
    return { target: null, reason: 'unsupported-wand-target' };
  }
  return { target, reason: null };
}

function classifySnapshot(snapshot, identity) {
  const negative = (reason) => ({ connected: false, reason });
  if (!isRecord(snapshot) || snapshot.overflow !== false || !Array.isArray(snapshot.nodes)
      || !Number.isSafeInteger(snapshot.nodeCount) || snapshot.nodeCount !== snapshot.nodes.length
      || snapshot.nodes.length > MAX_PLAY_BUTTONS) {
    return negative('malformed-sidebar-state');
  }
  if (snapshot.nodes.length === 0) return negative('matching-view-not-found');

  // The sidebar can contain placeholder elements without a bound Aurelia view.
  // Only a view that identifies this exact game can affect its status; unrelated
  // placeholders must not hide valid process-specific trainer evidence.
  const matches = snapshot.nodes.map((node) => ({ node, parsedId: isRecord(node) ? parseGameInfoId(node.gameInfoId) : null }))
    .filter((entry) => entry.parsedId?.value === identity.numericGameId);
  if (matches.length === 0) return negative('matching-view-not-found');
  if (matches.some(({ node, parsedId }) => !parsedId.canonical || node.vmPresent !== true
      || node.gameInfoPresent !== true)) {
    return negative('malformed-sidebar-view');
  }
  if (matches.length !== 1) return negative('ambiguous-matching-view');

  const view = matches[0].node;
  if (view.runningTrainerPresent !== true) return negative('trainer-not-active');
  if (view.trainerInfoPresent !== true
      || typeof view.runningTrainerGameId !== 'number'
      || !Number.isSafeInteger(view.runningTrainerGameId)
      || typeof view.trainerProcessId !== 'number'
      || !Number.isSafeInteger(view.trainerProcessId)
      || typeof view.trainerId !== 'string'
      || view.trainerId.length === 0
      || typeof view.runningTrainerInfoId !== 'string'
      || view.runningTrainerInfoId.length === 0) {
    return negative('malformed-trainer-state');
  }
  if (view.runningTrainerGameId !== identity.numericGameId) return negative('different-game');
  if (view.trainerProcessId !== identity.processId) return negative('different-process');
  if (view.trainerId !== view.runningTrainerInfoId) return negative('different-trainer');
  if (view.state !== 'playing') {
    return view.state === 'loading' || view.state === 'not-playing'
      ? negative('trainer-not-active')
      : negative('malformed-matching-sidebar-view');
  }
  return { connected: true, reason: 'trainer-active' };
}

function result(connected, reason, identity, clock) {
  return {
    connected,
    reason,
    gameId: identity.gameId,
    processId: identity.processId,
    observedUtc: clock().toISOString()
  };
}

function readCdpTargets(deadline) {
  const remaining = deadline - Date.now();
  if (remaining <= 0) return Promise.reject(new Error('Wand CDP probe timed out'));

  return new Promise((resolve, reject) => {
    let settled = false;
    let body = '';
    let bodyBytes = 0;
    const finish = (error, value) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      if (error) reject(error);
      else resolve(value);
    };

    const request = http.get(CDP_LIST_URL, (response) => {
      if (response.statusCode !== 200) {
        response.resume();
        finish(new Error(`Wand CDP target list returned HTTP ${response.statusCode || 'unknown'}`));
        return;
      }
      response.setEncoding('utf8');
      response.on('data', (chunk) => {
        bodyBytes += Buffer.byteLength(chunk, 'utf8');
        if (bodyBytes > MAX_TARGET_BYTES) {
          request.destroy(new Error('Wand CDP target list is too large'));
          return;
        }
        body += chunk;
      });
      response.on('end', () => {
        try {
          const targets = JSON.parse(body);
          if (!Array.isArray(targets)) throw new Error('CDP target list is not an array');
          finish(null, targets);
        } catch (error) {
          finish(new Error(`Wand CDP target list is invalid: ${error.message}`));
        }
      });
      response.on('aborted', () => finish(new Error('Wand CDP target response was interrupted')));
      response.on('error', (error) => finish(error));
    });

    const timeoutMs = Math.min(HTTP_TIMEOUT_MS, remaining);
    const timer = setTimeout(() => request.destroy(new Error('Wand CDP target request timed out')), timeoutMs);
    request.on('error', (error) => finish(error));
  });
}

function waitForSocketOpen(socket, deadline) {
  return new Promise((resolve, reject) => {
    const timeoutMs = Math.min(SOCKET_STEP_TIMEOUT_MS, deadline - Date.now());
    if (timeoutMs <= 0) return reject(new Error('Wand CDP probe timed out'));

    const cleanup = () => {
      clearTimeout(timer);
      socket.removeEventListener('open', onOpen);
      socket.removeEventListener('error', onError);
      socket.removeEventListener('close', onClose);
    };
    const onOpen = () => { cleanup(); resolve(); };
    const onError = () => { cleanup(); reject(new Error('Wand CDP WebSocket could not be opened')); };
    const onClose = () => { cleanup(); reject(new Error('Wand CDP WebSocket closed before opening')); };
    const timer = setTimeout(() => {
      cleanup();
      reject(new Error('Wand CDP WebSocket open timed out'));
    }, timeoutMs);
    socket.addEventListener('open', onOpen, { once: true });
    socket.addEventListener('error', onError, { once: true });
    socket.addEventListener('close', onClose, { once: true });
  });
}

function sendRuntimeEvaluation(socket, expression, deadline) {
  return new Promise((resolve, reject) => {
    const timeoutMs = Math.min(SOCKET_STEP_TIMEOUT_MS, deadline - Date.now());
    if (timeoutMs <= 0) return reject(new Error('Wand CDP probe timed out'));

    const cleanup = () => {
      clearTimeout(timer);
      socket.removeEventListener('message', onMessage);
      socket.removeEventListener('error', onError);
      socket.removeEventListener('close', onClose);
    };
    const onMessage = (event) => {
      let message;
      try { message = JSON.parse(event.data); }
      catch { cleanup(); reject(new Error('Wand CDP returned an invalid message')); return; }
      if (message.id !== 1) return;
      cleanup();
      if (message.error) {
        reject(new Error('Wand CDP Runtime.evaluate failed'));
        return;
      }
      if (!message.result || message.result.exceptionDetails
          || !message.result.result || !Object.hasOwn(message.result.result, 'value')) {
        reject(new Error('Wand CDP could not read the sidebar view models'));
        return;
      }
      resolve(message.result.result.value);
    };
    const onError = () => { cleanup(); reject(new Error('Wand CDP WebSocket failed during evaluation')); };
    const onClose = () => { cleanup(); reject(new Error('Wand CDP WebSocket closed during evaluation')); };
    const timer = setTimeout(() => {
      cleanup();
      reject(new Error('Wand CDP Runtime.evaluate timed out'));
    }, timeoutMs);
    socket.addEventListener('message', onMessage);
    socket.addEventListener('error', onError, { once: true });
    socket.addEventListener('close', onClose, { once: true });
    try {
      socket.send(JSON.stringify({
        id: 1,
        method: 'Runtime.evaluate',
        params: {
          expression,
          returnByValue: true,
          awaitPromise: false,
          silent: true,
          userGesture: false
        }
      }));
    } catch (error) {
      cleanup();
      reject(error);
    }
  });
}

async function evaluateWandPage(target, expression, deadline) {
  if (typeof WebSocket !== 'function') throw new Error('This Node runtime does not provide WebSocket');
  const socket = new WebSocket(target.webSocketDebuggerUrl);
  try {
    await waitForSocketOpen(socket, deadline);
    return await sendRuntimeEvaluation(socket, expression, deadline);
  } finally {
    try {
      if (socket.readyState === 0 || socket.readyState === 1) socket.close();
    } catch { /* Best-effort close for this read-only diagnostic connection. */ }
  }
}

async function probe(gameIdArg, processIdArg, dependencies = {}) {
  const identity = parseExpectedIdentity(gameIdArg, processIdArg);
  const clock = dependencies.clock || (() => new Date());
  const deadline = Date.now() + TOTAL_TIMEOUT_MS;
  const getTargets = dependencies.getTargets || readCdpTargets;
  const evaluate = dependencies.evaluate || evaluateWandPage;

  const targets = await getTargets(deadline);
  const selected = selectWandTarget(targets);
  if (!selected.target) return result(false, selected.reason, identity, clock);

  const snapshot = await evaluate(selected.target, READ_ONLY_EXPRESSION, deadline);
  const verdict = classifySnapshot(snapshot, identity);
  return result(verdict.connected, verdict.reason, identity, clock);
}

function parseTraceIdentityArguments(processNameArg, creationFileTimeArg, attemptStartedUtcArg, processIdArg) {
  const supplied = [processNameArg, creationFileTimeArg, attemptStartedUtcArg]
    .filter((value) => value !== undefined);
  if (supplied.length === 0) return null;
  if (supplied.length !== 3 || typeof processNameArg !== 'string'
      || processIdArg === undefined
      || !processNameArg.trim() || processNameArg.trim().length > 128
      || /[\\/\u0000-\u001f]/.test(processNameArg.trim())
      || typeof creationFileTimeArg !== 'string' || !/^[0-9A-Fa-f]{16}$/.test(creationFileTimeArg)
      || typeof attemptStartedUtcArg !== 'string' || !Number.isFinite(Date.parse(attemptStartedUtcArg))) {
    throw new Error('trainer trace identity arguments are invalid');
  }
  return {
    expectedProcessName: processNameArg.trim(),
    expectedProcessId: parsePositiveSafeInteger(String(processIdArg), 'processId'),
    processCreationFileTime: creationFileTimeArg.toUpperCase(),
    attemptStartedUtc: new Date(attemptStartedUtcArg).toISOString()
  };
}

function inspectHistoricalTrainerTrace(identity, dependencies = {}) {
  if (!identity) return { trainerObserved: false, reason: 'trace-identity-unavailable' };
  const localAppData = dependencies.localAppData === undefined ? process.env.LOCALAPPDATA : dependencies.localAppData;
  if (typeof localAppData !== 'string' || !path.isAbsolute(localAppData)) {
    return { trainerObserved: false, reason: 'trace-directory-unavailable' };
  }
  return inspectTrainerTraceDirectory(path.join(localAppData, 'Wand', 'logs', 'tophat'), identity, dependencies);
}

function writeJson(value) {
  process.stdout.write(`${JSON.stringify(value)}\n`);
}

async function main() {
  let identity;
  let traceIdentity;
  try {
    identity = parseExpectedIdentity(process.argv[2], process.argv[3]);
    traceIdentity = parseTraceIdentityArguments(process.argv[4], process.argv[5], process.argv[6], process.argv[3]);
  } catch (error) {
    const rawGameId = typeof process.argv[2] === 'string' ? process.argv[2] : '';
    const rawProcessId = typeof process.argv[3] === 'string' ? Number(process.argv[3]) : 0;
    writeJson({
      connected: false,
      reason: 'invalid-arguments',
      gameId: rawGameId,
      processId: Number.isSafeInteger(rawProcessId) && rawProcessId > 0 ? rawProcessId : 0,
      observedUtc: new Date().toISOString()
    });
    process.stderr.write(`${error.message}\n`);
    process.exitCode = 2;
    return;
  }

  let status;
  try {
    status = await probe(identity.gameId, String(identity.processId));
  } catch (error) {
    process.stderr.write(`${error && error.message ? error.message : String(error)}\n`);
    process.exitCode = 1;
    status = result(false, 'cdp-unavailable', identity, () => new Date());
  }
  const trainerTrace = inspectHistoricalTrainerTrace(traceIdentity);
  writeJson({ ...status, trainerTrace });
}

if (require.main === module) main();

module.exports = {
  CDP_LIST_URL,
  READ_ONLY_EXPRESSION,
  classifySnapshot,
  inspectHistoricalTrainerTrace,
  parseExpectedIdentity,
  parseTraceIdentityArguments,
  probe,
  selectWandTarget
};
