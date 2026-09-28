'use strict';

const crypto = require('crypto');
const http = require('http');
const { selectWandTarget } = require('./wand_trainer_status');

const MAX_TARGET_BYTES = 1024 * 1024;
const MAX_PREFLIGHT_PLAY_BUTTONS = 256;
const LAUNCH_TIMEOUT_MS = 10500;
const POLL_INTERVAL_MS = 120;
let launchNavigationAttempted = false;

class SafetyGateError extends Error {
  constructor(reason) {
    super(reason);
    this.name = 'SafetyGateError';
    this.reason = reason;
  }
}

function requireCanonicalId(value, label) {
  if (typeof value !== 'string' || !/^[1-9]\d*$/.test(value)) {
    throw new Error(`${label} must be a canonical positive decimal integer`);
  }
  const numeric = Number(value);
  if (!Number.isSafeInteger(numeric) || String(numeric) !== value) {
    throw new Error(`${label} must be a canonical safe integer`);
  }
  return value;
}

function normalizeWindowsPath(value) {
  if (typeof value !== 'string' || value.trim().length === 0) return '';
  let normalized = value.trim().replace(/\//g, '\\');
  const unc = normalized.startsWith('\\\\');
  normalized = normalized.replace(/^\\+/, '').replace(/\\+/g, '\\').replace(/\\$/, '');
  normalized = `${unc ? '\\\\' : ''}${normalized}`;
  if (!/^(?:[a-z]:\\|\\\\[^\\]+\\[^\\]+)/i.test(normalized)) return '';
  return normalized.toLowerCase();
}

function buildExpectedRegistration(titleIdArg, gameIdArg, titleNameArg, executablePathArg) {
  const titleId = requireCanonicalId(String(titleIdArg || ''), 'titleId');
  const gameId = requireCanonicalId(String(gameIdArg || ''), 'gameId');
  const executablePath = normalizeWindowsPath(String(executablePathArg || ''));
  if (!executablePath) throw new Error('executablePath must be an absolute Windows path');
  const titleName = typeof titleNameArg === 'string' ? titleNameArg.trim() : '';
  if (!titleName) throw new Error('titleName must be present');
  return {
    titleId,
    gameId,
    titleName,
    executablePath,
    installationSku: `${gameId}_${executablePath}`
  };
}

function validateLaunchNavigation(evaluation, titleId, gameId) {
  if (!evaluation || evaluation.exceptionDetails || typeof evaluation.result?.value !== 'string') {
    throw new Error('Wand did not confirm the auto-launch route navigation');
  }
  const href = evaluation.result.value;
  let route;
  try { route = new URL(href).hash; }
  catch { throw new Error('Wand returned an invalid route after auto-launch navigation'); }
  const match = /^#\/app\/titles\/(\d+)\?(.*)$/.exec(route);
  const params = match && new URLSearchParams(match[2]);
  if (!match || match[1] !== titleId || params.get('autoLaunch') !== 'true' || params.get('gameId') !== gameId) {
    throw new Error('Wand returned a different route than the requested exact game auto-launch');
  }
  return href;
}

// Returns a plain, bounded snapshot plus private DOM references used only by
// the exact UI actions below. No action is taken while collecting a snapshot.
function readWandPage() {
  const MAX_PREFLIGHT_PLAY_BUTTONS = 256;
  const visible = (element) => {
    if (!element || !element.isConnected || element.getClientRects().length === 0) return false;
    const style = getComputedStyle(element);
    return style.display !== 'none' && style.visibility !== 'hidden' && Number(style.opacity) !== 0;
  };
  const viewModel = (element) => element && element.au && element.au.controller
    ? element.au.controller.viewModel : null;
  const scopedViewModels = (element) => {
    const controller = element && element.au && element.au.controller;
    const scope = controller && controller.scope;
    const candidates = [
      controller && controller.viewModel,
      scope && scope.bindingContext,
      scope && scope.overrideContext && scope.overrideContext.bindingContext,
      scope && scope.controller && scope.controller.viewModel,
      scope && scope.viewModelScope && scope.viewModelScope.bindingContext
    ];
    return candidates.filter((value, index) => value && typeof value === 'object'
      && candidates.indexOf(value) === index);
  };
  const idText = (value) => value === null || value === undefined ? null : String(value);
  const versionFingerprint = (value) => {
    if (value === null || value === undefined) return '';
    if (typeof value !== 'object') return `${typeof value}:${String(value)}`;
    const keys = ['id', 'versionId', 'version', 'name', 'buildId', 'build', 'label'];
    const fields = [];
    for (const key of keys) {
      const field = value[key];
      if (typeof field === 'string' || (typeof field === 'number' && Number.isFinite(field))) {
        fields.push([key, String(field)]);
      }
    }
    return JSON.stringify(fields);
  };
  const installation = (value) => value && typeof value === 'object' ? {
    sku: typeof value.sku === 'string' ? value.sku : null,
    location: typeof value.location === 'string' ? value.location : null
  } : null;
  const mainButtons = (host) => host
    ? Array.from(host.querySelectorAll('play-button button.play-button__main-button')) : [];
  const control = (button) => ({
    count: button ? 1 : 0,
    visible: visible(button),
    disabled: !button || button.disabled === true || button.matches(':disabled')
      || !!button.closest('[inert]') || !!button.closest('.disabled'),
    text: button ? String(button.innerText || '').trim() : ''
  });

  const headers = Array.from(document.querySelectorAll('title-header'));
  const header = headers.length === 1 ? headers[0] : null;
  const headerVm = viewModel(header);
  const selectedGame = headerVm && headerVm.selectedGame;
  const selectedTrainer = headerVm && headerVm.selectedTrainer;
  const headerTrainerButtons = header ? Array.from(header.querySelectorAll('trainer-play-button')) : [];
  const headerTrainerButton = headerTrainerButtons.length === 1 ? headerTrainerButtons[0] : null;
  const headerTrainerVm = viewModel(headerTrainerButton);
  const headerMainButtons = mainButtons(headerTrainerButton);
  const headerMainButton = headerMainButtons.length === 1 ? headerMainButtons[0] : null;
  const buttonVersion = headerTrainerVm && headerTrainerVm.preferredGameVersion;
  const headerNotes = selectedTrainer && selectedTrainer.blueprint
    ? selectedTrainer.blueprint.notes : null;

  const sidebarPlayButtons = Array.from(document.querySelectorAll('sidebar-play-button'));
  const sidebarTrainerStates = sidebarPlayButtons.slice(0, MAX_PREFLIGHT_PLAY_BUTTONS).map((element) => {
    const vm = viewModel(element);
    const gameInfo = vm && vm.gameInfo;
    const visibility = vm && vm.trainerVisibility;
    const runningTrainer = visibility && visibility.runningTrainer;
    const trainerInfo = runningTrainer && runningTrainer.info;
    const activeTrainer = vm && vm.trainerService && vm.trainerService.trainer;
    const process = activeTrainer && activeTrainer.process;
    return {
      viewModelPresent: !!vm,
      gameInfoId: idText(gameInfo && gameInfo.id),
      state: vm && vm.state,
      runningTrainerPresent: !!runningTrainer,
      runningTrainerInfoId: idText(trainerInfo && trainerInfo.id),
      runningTrainerGameId: idText(trainerInfo && trainerInfo.gameId),
      trainerProcessId: process && process.id
    };
  });

  const dialogElements = Array.from(document.querySelectorAll('ux-dialog'));
  const dialogRecords = dialogElements.map((element) => {
    const wrappers = Array.from(element.querySelectorAll('.trainer-notes-dialog-wrapper'));
    const wrapper = wrappers.length === 1 ? wrappers[0] : null;
    const candidateNodes = [element, ...Array.from(element.querySelectorAll('*'))];
    for (let ancestor = element.parentElement, depth = 0; ancestor && depth < 6; ancestor = ancestor.parentElement, depth++) {
      candidateNodes.push(ancestor);
    }
    const candidateVms = [];
    for (const node of candidateNodes) {
      for (const vm of scopedViewModels(node)) {
        if (vm.config && vm.config.selectedTrainer
            && typeof vm.handlePlayButtonClick === 'function'
            && typeof vm.toggleDontShowAgain === 'function'
            && !candidateVms.includes(vm)) candidateVms.push(vm);
      }
    }
    const notesVm = candidateVms.length === 1 ? candidateVms[0] : null;
    const config = notesVm && notesVm.config;
    const configTrainer = config && config.selectedTrainer;
    const contents = wrapper ? Array.from(wrapper.querySelectorAll('.trainer-notes-dialog-wrapper-body-content')) : [];
    const scrollElements = wrapper ? Array.from(wrapper.querySelectorAll('.trainer-notes-dialog-wrapper-body-content .scroll-wrapper')) : [];
    const noteParagraphs = wrapper ? Array.from(wrapper.querySelectorAll('.trainer-notes-dialog-wrapper-body-content .scroll-wrapper > p')) : [];
    const checkboxControls = wrapper ? Array.from(wrapper.querySelectorAll('.trainer-notes-dialog-wrapper-footer-left')) : [];
    const checkboxControl = checkboxControls.length === 1 ? checkboxControls[0] : null;
    const toggleElements = checkboxControl ? Array.from(checkboxControl.querySelectorAll('.toggle-wrapper')) : [];
    const checkboxLabel = checkboxControl && checkboxControl.querySelector('.label');
    const notePlayButtons = wrapper ? Array.from(wrapper.querySelectorAll('trainer-play-button')) : [];
    const notePlayButton = notePlayButtons.length === 1 ? notePlayButtons[0] : null;
    const notePlayVm = viewModel(notePlayButton);
    const noteMainButtons = mainButtons(notePlayButton);
    const noteMainButton = noteMainButtons.length === 1 ? noteMainButtons[0] : null;
    const scrollElement = scrollElements.length === 1 ? scrollElements[0] : null;
    const paragraph = noteParagraphs.length === 1 ? noteParagraphs[0] : null;
    const noteText = paragraph ? String(paragraph.innerText || '').trim() : '';
    const noteRaw = config && typeof config.trainerNotes === 'string' ? config.trainerNotes : null;
    const dialogText = String(element.innerText || '').trim();
    const scrollTop = scrollElement ? Number(scrollElement.scrollTop) : null;
    const scrollHeight = scrollElement ? Number(scrollElement.scrollHeight) : null;
    const clientHeight = scrollElement ? Number(scrollElement.clientHeight) : null;
    const isScrollable = notesVm && typeof notesVm.isScrollable === 'boolean' ? notesVm.isScrollable : null;
    const hasScrolledToBottom = notesVm && typeof notesVm.hasScrolledToBottom === 'boolean'
      ? notesVm.hasScrolledToBottom : null;
    const dontShowValue = notesVm ? notesVm.dontShowToggled : null;
    const checkboxStateValid = !!notesVm && (typeof dontShowValue === 'boolean' || dontShowValue === undefined);
    const dontShowToggled = checkboxStateValid ? dontShowValue === true : null;
    const toggleEnabled = toggleElements.length === 1
      && toggleElements[0].classList.contains('enabled');

    return {
      notesShape: element.classList.contains('trainer-notes-dialog')
        && element.classList.contains('fullscreen-dialog')
        && wrappers.length === 1,
      wrapperCount: wrappers.length,
      notesViewModelCount: candidateVms.length,
      notesViewModelPresent: !!notesVm,
      notesMethodsPresent: !!notesVm && typeof notesVm.handlePlayButtonClick === 'function'
        && typeof notesVm.toggleDontShowAgain === 'function',
      configTitleId: idText(configTrainer && configTrainer.titleId),
      configGameId: idText(configTrainer && configTrainer.gameId),
      configTrainerId: idText(configTrainer && configTrainer.id),
      selectedTrainerReferenceMatches: !!selectedTrainer && configTrainer === selectedTrainer,
      noteRaw,
      selectedTrainerNoteRaw: typeof headerNotes === 'string' ? headerNotes : null,
      noteText,
      noteTextVisible: visible(paragraph),
      contentCount: contents.length,
      contentVisible: visible(contents[0]),
      scrollElementCount: scrollElements.length,
      scrollVisible: visible(scrollElement),
      scrollTop,
      scrollHeight,
      clientHeight,
      isScrollable,
      hasScrolledToBottom,
      dontShowToggled,
      checkboxStateValid,
      checkboxCount: checkboxControls.length,
      checkboxVisible: visible(checkboxControl),
      checkboxLabel: checkboxLabel ? String(checkboxLabel.innerText || '').trim() : '',
      toggleCount: toggleElements.length,
      toggleEnabled,
      notePlayButtonCount: notePlayButtons.length,
      notePlayButtonVmPresent: !!notePlayVm,
      notePlayTrainerId: idText(notePlayVm && notePlayVm.trainerInfo && notePlayVm.trainerInfo.id),
      notePlayGameId: idText(notePlayVm && notePlayVm.trainerInfo && notePlayVm.trainerInfo.gameId),
      notePlayTitleId: idText(notePlayVm && notePlayVm.trainerInfo && notePlayVm.trainerInfo.titleId),
      notePlayTrainerReferenceMatches: !!configTrainer && !!notePlayVm
        && notePlayVm.trainerInfo === configTrainer,
      notePlayButtonState: notePlayVm && notePlayVm.buttonState,
      notePlayInstallation: installation(notePlayVm && notePlayVm.preferredInstallation),
      notePlayVersionFingerprint: versionFingerprint(notePlayVm && notePlayVm.preferredGameVersion),
      notePlayVersionMatchesHeader: !!notePlayVm && (notePlayVm.preferredGameVersion === buttonVersion
        || (!notePlayVm.preferredGameVersion && !buttonVersion)
        || versionFingerprint(notePlayVm.preferredGameVersion) === versionFingerprint(buttonVersion)),
      noteMainButtonCount: noteMainButtons.length,
      noteMainButton: control(noteMainButton),
      dialogText,
      isScrollableExpectedByDom: scrollElement ? scrollHeight > clientHeight : null,
      refs: { notesVm, scrollElement, checkboxControl, noteMainButton }
    };
  });

  const headerSnapshot = {
    viewModelPresent: !!headerVm,
    titleId: idText(headerVm && headerVm.titleId),
    modelId: idText(headerVm && headerVm.model && headerVm.model.id),
    modelName: headerVm && headerVm.model && typeof headerVm.model.name === 'string'
      ? headerVm.model.name : null,
    selectedGameId: idText(selectedGame && selectedGame.id),
    selectedTrainerId: idText(selectedTrainer && selectedTrainer.id),
    selectedTrainerTitleId: idText(selectedTrainer && selectedTrainer.titleId),
    selectedTrainerGameId: idText(selectedTrainer && selectedTrainer.gameId),
    selectedTrainerNotes: typeof headerNotes === 'string' ? headerNotes : null,
    preferredInstallation: installation(headerVm && headerVm.preferredInstallation),
    trainerButtonCount: headerTrainerButtons.length,
    trainerButtonVmPresent: !!headerTrainerVm,
    trainerButtonTrainerId: idText(headerTrainerVm && headerTrainerVm.trainerInfo && headerTrainerVm.trainerInfo.id),
    trainerButtonTitleId: idText(headerTrainerVm && headerTrainerVm.trainerInfo && headerTrainerVm.trainerInfo.titleId),
    trainerButtonGameId: idText(headerTrainerVm && headerTrainerVm.trainerInfo && headerTrainerVm.trainerInfo.gameId),
    trainerButtonTrainerReferenceMatches: !!selectedTrainer && !!headerTrainerVm
      && headerTrainerVm.trainerInfo === selectedTrainer,
    trainerButtonState: headerTrainerVm && headerTrainerVm.buttonState,
    trainerButtonInstallation: installation(headerTrainerVm && headerTrainerVm.preferredInstallation),
    trainerButtonVersionFingerprint: versionFingerprint(buttonVersion),
    trainerButtonVersionPresent: buttonVersion !== null && buttonVersion !== undefined,
    mainButtonCount: headerMainButtons.length,
    mainButton: control(headerMainButton)
  };

  const snapshot = {
    href: location.href,
    titleHeaderCount: headers.length,
    header: headerSnapshot,
    dialogCount: dialogElements.length,
    dialogs: dialogRecords.map(({ refs, ...record }) => record),
    sidebarPlayButtonCount: sidebarPlayButtons.length,
    sidebarPlayButtonOverflow: sidebarPlayButtons.length > MAX_PREFLIGHT_PLAY_BUTTONS,
    sidebarTrainerStates
  };
  return {
    snapshot,
    refs: {
      headerMainButton,
      dialogs: dialogRecords.map((record) => record.refs)
    }
  };
}

function validatePreNavigationSnapshot(snapshot) {
  const blocked = (reason, active = null) => ({
    ok: false,
    state: 'blocked',
    navigated: false,
    playDispatched: false,
    reason,
    ...(active && active.gameId ? { activeTrainerGameId: active.gameId } : {}),
    ...(active && Number.isSafeInteger(active.processId) ? { activeTrainerProcessId: active.processId } : {})
  });
  const positiveId = (value) => {
    if (Number.isSafeInteger(value) && value > 0) return String(value);
    if (typeof value !== 'string' || !/^[1-9]\d*$/.test(value)) return null;
    const numeric = Number(value);
    return Number.isSafeInteger(numeric) && String(numeric) === value ? value : null;
  };
  const positiveProcessId = (value) => {
    if (Number.isSafeInteger(value) && value > 0) return value;
    if (typeof value !== 'string' || !/^[1-9]\d*$/.test(value)) return null;
    const numeric = Number(value);
    return Number.isSafeInteger(numeric) && String(numeric) === value ? numeric : null;
  };
  const busyStates = new Set(['loading', 'playing', 'playing_other', 'stopping']);
  const idleStates = new Set(['play', 'not-playing']);

  if (!snapshot || typeof snapshot !== 'object'
      || !Number.isSafeInteger(snapshot.sidebarPlayButtonCount)
      || snapshot.sidebarPlayButtonCount < 0
      || typeof snapshot.sidebarPlayButtonOverflow !== 'boolean'
      || !Array.isArray(snapshot.sidebarTrainerStates)
      || snapshot.sidebarPlayButtonCount !== snapshot.sidebarTrainerStates.length
      || snapshot.sidebarPlayButtonCount > MAX_PREFLIGHT_PLAY_BUTTONS
      || snapshot.sidebarPlayButtonOverflow) {
    return blocked('trainer-state-unavailable-before-navigation');
  }
  if (snapshot.sidebarTrainerStates.length === 0) {
    return blocked('trainer-state-unavailable-before-navigation');
  }

  for (const node of snapshot.sidebarTrainerStates) {
    if (!node || typeof node !== 'object' || node.viewModelPresent !== true
        || !positiveId(node.gameInfoId) || typeof node.runningTrainerPresent !== 'boolean'
        || typeof node.state !== 'string') {
      return blocked('trainer-state-unavailable-before-navigation');
    }
    if (!busyStates.has(node.state) && !idleStates.has(node.state)) {
      return blocked('trainer-state-unavailable-before-navigation');
    }
    if (node.runningTrainerPresent || busyStates.has(node.state)) {
      const gameId = positiveId(node.runningTrainerGameId);
      const processId = positiveProcessId(node.trainerProcessId);
      return blocked('trainer-was-already-launching', { gameId, processId });
    }
  }

  const activeHeaderState = snapshot.header && snapshot.header.trainerButtonState;
  if (typeof activeHeaderState === 'string' && busyStates.has(activeHeaderState)) {
    return blocked('trainer-was-already-launching');
  }

  return { ok: true, state: 'idle' };
}

async function inspectPreNavigationState(client) {
  try {
    const current = await client.evaluate(readSnapshotExpression());
    const preflight = validatePreNavigationSnapshot(current);
    return preflight.ok
      ? { ok: true, state: 'idle', navigated: false, playDispatched: false }
      : preflight;
  } catch {
    return {
      ok: false,
      state: 'unknown',
      navigated: false,
      playDispatched: false,
      reason: 'trainer-state-unavailable-before-navigation'
    };
  }
}

async function prepareLaunchNavigation(client, expected, nonce) {
  launchNavigationAttempted = false;
  const preflight = await inspectPreNavigationState(client);
  if (!preflight.ok) return preflight;

  const hash = `#/app/titles/${expected.titleId}?autoLaunch=true&gameId=${expected.gameId}&trainerId=&launchNonce=${nonce}`;
  launchNavigationAttempted = true;
  try {
    const evaluation = await client.evaluate(`location.hash=${JSON.stringify(hash)};location.href`, true);
    const href = validateLaunchNavigation({ result: { value: evaluation } }, expected.titleId, expected.gameId);
    return { ok: true, navigated: true, href };
  } catch {
    // A CDP disconnect may happen after the renderer accepted the autoLaunch
    // route. Report the attempt conservatively so callers never issue a second
    // launch through the protocol URI on an ambiguous outcome.
    return {
      ok: false,
      state: 'unknown',
      navigated: true,
      playDispatched: false,
      reason: 'launch-navigation-outcome-unknown'
    };
  }
}

function validateLaunchSnapshot(snapshot, expected, expectedNoteEvidence = null) {
  const blocked = (reason) => ({ ok: false, state: 'blocked', reason });
  const waiting = (reason) => ({ ok: true, state: 'wait', reason });
  const hasExpectedRoute = () => {
    if (!snapshot || typeof snapshot.href !== 'string') return false;
    try {
      const route = new URL(snapshot.href).hash;
      const match = /^#\/app\/titles\/(\d+)\?(.*)$/.exec(route);
      if (!match || match[1] !== expected.titleId) return false;
      const params = new URLSearchParams(match[2]);
      return params.get('autoLaunch') === 'true' && params.get('gameId') === expected.gameId;
    } catch { return false; }
  };
  const normalizeId = (value) => {
    if (typeof value === 'number' && Number.isSafeInteger(value) && value > 0) return String(value);
    if (typeof value !== 'string' || !/^[1-9]\d*$/.test(value)) return null;
    const numeric = Number(value);
    return Number.isSafeInteger(numeric) && String(numeric) === value ? value : null;
  };
  const normalizePath = (value) => {
    if (typeof value !== 'string' || value.trim().length === 0) return '';
    let normalized = value.trim().replace(/\//g, '\\');
    const unc = normalized.startsWith('\\\\');
    normalized = normalized.replace(/^\\+/, '').replace(/\\+/g, '\\').replace(/\\$/, '');
    normalized = `${unc ? '\\\\' : ''}${normalized}`;
    if (!/^(?:[a-z]:\\|\\\\[^\\]+\\[^\\]+)/i.test(normalized)) return '';
    return normalized.toLowerCase();
  };
  const sameInstallation = (installation) => !!installation
    && typeof installation.sku === 'string'
    && typeof installation.location === 'string'
    && installation.sku.toLowerCase() === expected.installationSku
    && normalizePath(installation.location) === expected.executablePath;
  const legalOrConsent = /\b(?:legal|consent|agree|accept|terms?(?:\s+of\s+(?:service|use))?|eula|end[- ]user license|licen[cs]e agreement|privacy policy|waiver|authori[sz](?:e|ation))\b/i;

  if (!snapshot || typeof snapshot !== 'object' || !expected || typeof expected !== 'object') {
    return blocked('malformed-launch-snapshot');
  }
  if (!hasExpectedRoute()) return blocked('route-mismatch');
  if (!Number.isSafeInteger(snapshot.titleHeaderCount) || snapshot.titleHeaderCount < 0) {
    return blocked('malformed-title-header-state');
  }
  if (snapshot.titleHeaderCount === 0) return waiting('title-header-not-ready');
  if (snapshot.titleHeaderCount !== 1 || !snapshot.header || typeof snapshot.header !== 'object') {
    return blocked('ambiguous-title-header');
  }

  const header = snapshot.header;
  if (header.viewModelPresent !== true) return waiting('title-header-view-model-not-ready');
  const actualTitleId = normalizeId(header.titleId);
  const actualModelId = normalizeId(header.modelId);
  if (!actualTitleId || !actualModelId) return waiting('title-model-not-ready');
  if (actualTitleId !== expected.titleId || actualModelId !== expected.titleId) return blocked('different-title');
  if (typeof header.modelName !== 'string' || header.modelName.trim().length === 0) return waiting('title-name-not-ready');
  if (header.modelName.trim().toLowerCase() !== expected.titleName.trim().toLowerCase()) return blocked('different-title-name');

  const selectedGameId = normalizeId(header.selectedGameId);
  if (!selectedGameId) return waiting('selected-game-not-ready');
  if (selectedGameId !== expected.gameId) return blocked('different-selected-game');
  const trainerId = typeof header.selectedTrainerId === 'string' ? header.selectedTrainerId.trim() : '';
  if (!trainerId) return waiting('selected-trainer-not-ready');
  if (normalizeId(header.selectedTrainerTitleId) !== expected.titleId
      || normalizeId(header.selectedTrainerGameId) !== expected.gameId) {
    return blocked('selected-trainer-identity-mismatch');
  }

  if (header.preferredInstallation && !sameInstallation(header.preferredInstallation)) {
    return blocked('selected-installation-mismatch');
  }
  if (!sameInstallation(header.trainerButtonInstallation)) {
    return header.trainerButtonInstallation ? blocked('trainer-button-installation-mismatch')
      : waiting('trainer-button-installation-not-ready');
  }
  if (header.trainerButtonCount !== 1) {
    return header.trainerButtonCount === 0 ? waiting('trainer-play-button-not-ready')
      : blocked('ambiguous-trainer-play-button');
  }
  if (header.trainerButtonVmPresent !== true) return waiting('trainer-play-view-model-not-ready');
  if (header.trainerButtonTrainerReferenceMatches !== true
      || header.trainerButtonTrainerId !== trainerId
      || normalizeId(header.trainerButtonTitleId) !== expected.titleId
      || normalizeId(header.trainerButtonGameId) !== expected.gameId) {
    return blocked('trainer-play-button-identity-mismatch');
  }
  if (typeof header.trainerButtonVersionFingerprint !== 'string'
      || typeof header.trainerButtonVersionPresent !== 'boolean') return blocked('selected-version-state-malformed');
  if (header.selectedTrainerNotes !== null && typeof header.selectedTrainerNotes !== 'string') {
    return blocked('malformed-trainer-notes');
  }

  if (snapshot.dialogCount !== snapshot.dialogs?.length || !Array.isArray(snapshot.dialogs)) {
    return blocked('malformed-dialog-state');
  }
  if (snapshot.dialogCount > 1) return blocked('ambiguous-dialogs');
  if (snapshot.dialogCount === 0) {
    if (header.trainerButtonState === 'loading' || header.trainerButtonState === 'playing'
        || header.trainerButtonState === 'playing_other' || header.trainerButtonState === 'stopping') {
      return { ok: true, state: 'launching', trainerId };
    }
    if (header.trainerButtonState !== 'play') {
      if (!header.trainerButtonState) return waiting('trainer-button-state-not-ready');
      return blocked('trainer-button-not-playable');
    }
    if (header.mainButtonCount !== 1) {
      return header.mainButtonCount === 0 ? waiting('play-control-not-ready') : blocked('ambiguous-play-controls');
    }
    if (!header.mainButton.visible || header.mainButton.disabled || !header.mainButton.text) {
      return blocked('play-control-not-actionable');
    }
    return { ok: true, state: 'ready', trainerId };
  }

  const dialog = snapshot.dialogs[0];
  if (!dialog || dialog.notesShape !== true || dialog.wrapperCount !== 1) return blocked('unsupported-dialog');
  if (dialog.notesViewModelCount !== 1 || dialog.notesViewModelPresent !== true
      || dialog.notesMethodsPresent !== true) return blocked('ambiguous-trainer-notes-view-model');
  if (dialog.configTitleId !== expected.titleId || dialog.configGameId !== expected.gameId
      || dialog.configTrainerId !== trainerId || dialog.selectedTrainerReferenceMatches !== true) {
    return blocked('trainer-notes-target-mismatch');
  }
  if (typeof dialog.noteRaw !== 'string' || dialog.noteRaw.trim().length === 0
      || typeof dialog.noteText !== 'string' || dialog.noteText.trim().length === 0
      || dialog.noteTextVisible !== true || dialog.selectedTrainerNoteRaw !== dialog.noteRaw) {
    return blocked('trainer-notes-content-not-verifiable');
  }
  if (expectedNoteEvidence && (expectedNoteEvidence.rawText !== dialog.noteRaw
      || expectedNoteEvidence.visibleText !== dialog.noteText)) {
    return blocked('trainer-notes-content-changed');
  }
  const allDialogText = `${dialog.dialogText || ''}\n${dialog.noteRaw}\n${dialog.noteText}`;
  if (legalOrConsent.test(allDialogText)) return blocked('legal-or-consent-dialog');

  if (dialog.contentCount !== 1 || dialog.scrollElementCount !== 1 || dialog.scrollVisible !== true
      || dialog.contentVisible !== true || dialog.noteTextVisible !== true) {
    return blocked('trainer-notes-reading-control-ambiguous');
  }
  if (![dialog.scrollTop, dialog.scrollHeight, dialog.clientHeight].every(Number.isFinite)
      || dialog.clientHeight <= 0 || dialog.scrollHeight < dialog.clientHeight) {
    return blocked('trainer-notes-scroll-state-malformed');
  }
  if (typeof dialog.isScrollable !== 'boolean' || typeof dialog.hasScrolledToBottom !== 'boolean'
      || dialog.isScrollable !== dialog.isScrollableExpectedByDom) {
    return waiting('trainer-notes-scroll-state-not-ready');
  }
  const atBottomByMetrics = dialog.scrollHeight - dialog.scrollTop - dialog.clientHeight < 5;
  if (dialog.hasScrolledToBottom !== atBottomByMetrics) return waiting('trainer-notes-scroll-state-not-ready');
  if (!dialog.isScrollable && !dialog.hasScrolledToBottom) return waiting('trainer-notes-scroll-state-not-ready');

  if (dialog.checkboxCount !== 1 || dialog.checkboxVisible !== true || dialog.toggleCount !== 1
      || !dialog.checkboxLabel || dialog.checkboxStateValid !== true
      || typeof dialog.dontShowToggled !== 'boolean'
      || dialog.toggleEnabled !== dialog.dontShowToggled) {
    return blocked('trainer-notes-checkbox-state-ambiguous');
  }
  if (dialog.notePlayButtonCount !== 1 || dialog.notePlayButtonVmPresent !== true
      || dialog.notePlayTrainerReferenceMatches !== true
      || dialog.notePlayTrainerId !== trainerId
      || normalizeId(dialog.notePlayTitleId) !== expected.titleId
      || normalizeId(dialog.notePlayGameId) !== expected.gameId) {
    return blocked('trainer-notes-play-target-mismatch');
  }
  if (!sameInstallation(dialog.notePlayInstallation)
      || dialog.notePlayVersionMatchesHeader !== true
      || dialog.notePlayVersionFingerprint !== header.trainerButtonVersionFingerprint) {
    return blocked('trainer-notes-version-mismatch');
  }
  if (dialog.notePlayButtonState !== 'play') return blocked('trainer-notes-play-not-ready');
  if (dialog.noteMainButtonCount !== 1 || !dialog.noteMainButton.visible
      || dialog.noteMainButton.disabled || !dialog.noteMainButton.text) {
    return blocked('trainer-notes-play-control-not-actionable');
  }
  return { ok: true, state: 'notes', trainerId };
}

function noteTextHash(rawText, visibleText) {
  return crypto.createHash('sha256').update(`${rawText}\0${visibleText}`, 'utf8').digest('hex');
}

function formatEvaluationFailure(result) {
  const exception = result && result.exceptionDetails;
  if (exception) {
    const remote = exception.exception || {};
    const className = typeof remote.className === 'string' && remote.className.length > 0
      ? remote.className.replace(/[^A-Za-z0-9_.-]/g, '').slice(0, 48) : 'EvaluationError';
    const raw = typeof remote.description === 'string' ? remote.description : '';
    const firstLine = raw.split(/[\r\n]/, 1)[0].replace(/(['"`]).*?\1/g, '[redacted]')
      .replace(/\b[a-z]:\\[^\s,;)]+/gi, '[path]')
      .replace(/\s+/g, ' ').slice(0, 160);
    const line = Number.isSafeInteger(exception.lineNumber) ? exception.lineNumber : null;
    const column = Number.isSafeInteger(exception.columnNumber) ? exception.columnNumber : null;
    return `${className}${firstLine ? `: ${firstLine}` : ''}${line === null ? '' : ` at ${line}:${column ?? 0}`}`;
  }
  const remoteType = result && result.result && typeof result.result.type === 'string'
    ? result.result.type : 'missing';
  return `Wand CDP returned ${remoteType} instead of a by-value result`;
}

function readSnapshotExpression() {
  return `(${readWandPage.toString()})().snapshot`;
}

function actionExpression(action, expected, noteEvidence = null) {
  const expectedJson = JSON.stringify(expected);
  const noteJson = JSON.stringify(noteEvidence);
  return `(() => {
    const current = (${readWandPage.toString()})();
    const gate = (${validateLaunchSnapshot.toString()})(current.snapshot, ${expectedJson}, ${noteJson});
    if (!gate.ok) return { performed: false, blocked: gate.state === 'blocked', reason: gate.reason };
    const dialogRefs = current.refs.dialogs[0];
    if (${JSON.stringify(action)} === 'click-header-play') {
      if (gate.state !== 'ready' || !current.refs.headerMainButton) return { performed: false, reason: 'header-play-state-changed' };
      current.refs.headerMainButton.click();
      return { performed: true, action: 'click-header-play' };
    }
    if (${JSON.stringify(action)} === 'scroll-notes') {
      if (gate.state !== 'notes' || !dialogRefs || !dialogRefs.scrollElement || !dialogRefs.notesVm) return { performed: false, reason: 'note-scroll-state-changed' };
      if (dialogRefs.notesVm.isScrollable && !dialogRefs.notesVm.hasScrolledToBottom) {
        if (dialogRefs.scrollElement.scrollHeight <= dialogRefs.scrollElement.clientHeight) return { performed: false, blocked: true, reason: 'note-scroll-metrics-changed' };
        dialogRefs.scrollElement.scrollTop = dialogRefs.scrollElement.scrollHeight;
        dialogRefs.scrollElement.dispatchEvent(new Event('scroll', { bubbles: true }));
        dialogRefs.notesVm.handleScroll();
      }
      return { performed: true, action: 'scroll-notes' };
    }
    if (${JSON.stringify(action)} === 'toggle-note-read') {
      if (gate.state !== 'notes' || !dialogRefs || !dialogRefs.notesVm || !dialogRefs.checkboxControl) return { performed: false, reason: 'note-checkbox-state-changed' };
      if (!dialogRefs.notesVm.dontShowToggled) dialogRefs.notesVm.toggleDontShowAgain();
      return { performed: dialogRefs.notesVm.dontShowToggled === true, action: 'toggle-note-read' };
    }
    if (${JSON.stringify(action)} === 'click-notes-play') {
      if (gate.state !== 'notes' || !dialogRefs || !dialogRefs.noteMainButton
          || !dialogRefs.notesVm || dialogRefs.notesVm.dontShowToggled !== true
          || (dialogRefs.notesVm.isScrollable && !dialogRefs.notesVm.hasScrolledToBottom)) {
        return { performed: false, reason: 'note-play-state-changed' };
      }
      dialogRefs.noteMainButton.click();
      return { performed: true, action: 'click-notes-play' };
    }
    return { performed: false, blocked: true, reason: 'unknown-action' };
  })()`;
}

function getJson(url) {
  return new Promise((resolve, reject) => {
    const request = http.get(url, (response) => {
      let body = '';
      let bodyBytes = 0;
      response.setEncoding('utf8');
      response.on('data', (chunk) => {
        bodyBytes += Buffer.byteLength(chunk, 'utf8');
        if (bodyBytes > MAX_TARGET_BYTES) request.destroy(new Error('CDP target response is too large'));
        else body += chunk;
      });
      response.on('end', () => {
        if (response.statusCode !== 200) {
          reject(new Error(`Wand CDP target list returned HTTP ${response.statusCode || 'unknown'}`));
          return;
        }
        try { resolve(JSON.parse(body)); }
        catch (error) { reject(error); }
      });
    });
    request.setTimeout(2500, () => request.destroy(new Error('Wand CDP target request timed out')));
    request.on('error', reject);
  });
}

function createCdpClient(target, deadline) {
  if (typeof WebSocket !== 'function') throw new Error('This Node runtime does not provide WebSocket');
  const socket = new WebSocket(target.webSocketDebuggerUrl);
  let nextId = 1;
  const pending = new Map();
  const timeoutMs = Math.max(1, Math.min(1000, deadline - Date.now()));
  const open = new Promise((resolve, reject) => {
    const finish = (error) => {
      clearTimeout(timer);
      socket.removeEventListener('open', onOpen);
      socket.removeEventListener('error', onError);
      socket.removeEventListener('close', onClose);
      if (error) reject(error);
      else resolve();
    };
    const onOpen = () => finish();
    const onError = () => finish(new Error('Wand CDP WebSocket could not be opened'));
    const onClose = () => finish(new Error('Wand CDP WebSocket closed before opening'));
    const timer = setTimeout(() => finish(new Error('Wand CDP WebSocket open timed out')), timeoutMs);
    socket.addEventListener('open', onOpen, { once: true });
    socket.addEventListener('error', onError, { once: true });
    socket.addEventListener('close', onClose, { once: true });
  });

  socket.addEventListener('message', (event) => {
    let message;
    try { message = JSON.parse(event.data); }
    catch {
      for (const waiter of pending.values()) waiter.reject(new Error('Wand CDP returned an invalid message'));
      pending.clear();
      return;
    }
    const waiter = pending.get(message.id);
    if (!waiter) return;
    pending.delete(message.id);
    if (message.error) waiter.reject(new Error('Wand CDP Runtime.evaluate failed'));
    else waiter.resolve(message.result);
  });
  socket.addEventListener('close', () => {
    for (const waiter of pending.values()) waiter.reject(new Error('Wand CDP socket closed'));
    pending.clear();
  });

  return {
    async evaluate(expression, userGesture = false) {
      await open;
      const remaining = deadline - Date.now();
      if (remaining <= 0) throw new Error('Wand CDP launch timed out');
      const id = nextId++;
      return new Promise((resolve, reject) => {
        const timer = setTimeout(() => {
          pending.delete(id);
          reject(new Error('Wand CDP Runtime.evaluate timed out'));
        }, Math.min(1500, remaining));
        pending.set(id, {
          resolve: (result) => { clearTimeout(timer); resolve(result); },
          reject: (error) => { clearTimeout(timer); reject(error); }
        });
        try {
          socket.send(JSON.stringify({
            id,
            method: 'Runtime.evaluate',
            params: { expression, returnByValue: true, awaitPromise: true, userGesture, silent: false }
          }));
        } catch (error) {
          clearTimeout(timer);
          pending.delete(id);
          reject(error);
        }
      }).then((result) => {
        if (!result || result.exceptionDetails || !result.result
            || !Object.hasOwn(result.result, 'value')) {
          throw new Error(formatEvaluationFailure(result));
        }
        return result.result.value;
      });
    },
    close() {
      try { if (socket.readyState === WebSocket.OPEN || socket.readyState === WebSocket.CONNECTING) socket.close(); }
      catch { /* Best-effort socket cleanup. */ }
    }
  };
}

function wait(delayMs, deadline) {
  const remaining = deadline - Date.now();
  if (remaining <= 0) return Promise.resolve();
  return new Promise((resolve) => setTimeout(resolve, Math.min(delayMs, remaining)));
}

async function readAndValidate(client, expected, deadline, noteEvidence = null) {
  const snapshot = await client.evaluate(readSnapshotExpression());
  const verdict = validateLaunchSnapshot(snapshot, expected, noteEvidence);
  return { snapshot, verdict };
}

async function waitForState(client, expected, deadline, noteEvidence, acceptStates) {
  let last = null;
  while (Date.now() < deadline) {
    last = await readAndValidate(client, expected, deadline, noteEvidence);
    if (last.verdict.state === 'blocked') throw new SafetyGateError(last.verdict.reason);
    if (acceptStates.includes(last.verdict.state)) return last;
    await wait(POLL_INTERVAL_MS, deadline);
  }
  if (last && last.verdict.state === 'blocked') throw new SafetyGateError(last.verdict.reason);
  throw new Error(`Wand launch state did not become safe before timeout (${last?.verdict.reason || 'no-snapshot'})`);
}

async function performAction(client, action, expected, deadline, noteEvidence = null) {
  const response = await client.evaluate(actionExpression(action, expected, noteEvidence), true);
  if (!response || response.performed !== true) {
    if (response && response.blocked) throw new SafetyGateError(response.reason || 'action-safety-check-failed');
    throw new Error(response && response.reason || `Wand action ${action} was not confirmed`);
  }
  return response;
}

async function dispatchVerifiedPlay(client, expected, deadline) {
  let current = await waitForState(client, expected, deadline, null, ['ready', 'notes', 'launching']);
  if (current.verdict.state === 'launching') throw new SafetyGateError('trainer-was-already-launching');

  if (current.verdict.state === 'ready') {
    await performAction(client, 'click-header-play', expected, deadline);
    current = await waitForState(client, expected, deadline, null, ['notes', 'launching']);
    if (current.verdict.state === 'launching') {
      return {
        playDispatched: true,
        noteAcknowledged: false,
        trainerId: current.verdict.trainerId,
        versionFingerprint: current.snapshot.header.trainerButtonVersionFingerprint,
        noteHash: null
      };
    }
  }

  let noteEvidence = {
    rawText: current.snapshot.dialogs[0].noteRaw,
    visibleText: current.snapshot.dialogs[0].noteText
  };
  const noteHash = noteTextHash(noteEvidence.rawText, noteEvidence.visibleText);
  current = await readAndValidate(client, expected, deadline, noteEvidence);
  if (current.verdict.state === 'blocked') throw new SafetyGateError(current.verdict.reason);
  if (current.verdict.state !== 'notes') throw new SafetyGateError('trainer-notes-dialog-changed-before-acknowledgement');

  const dialog = current.snapshot.dialogs[0];
  if (dialog.isScrollable && !dialog.hasScrolledToBottom) {
    await performAction(client, 'scroll-notes', expected, deadline, noteEvidence);
    current = await waitForState(client, expected, deadline, noteEvidence, ['notes']);
    if (!current.snapshot.dialogs[0].hasScrolledToBottom) {
      throw new SafetyGateError('trainer-notes-scroll-did-not-reach-bottom');
    }
  }

  if (!current.snapshot.dialogs[0].dontShowToggled) {
    await performAction(client, 'toggle-note-read', expected, deadline, noteEvidence);
    current = await waitForState(client, expected, deadline, noteEvidence, ['notes']);
  }
  if (!current.snapshot.dialogs[0].dontShowToggled) {
    throw new SafetyGateError('trainer-notes-acknowledgement-state-not-confirmed');
  }
  await performAction(client, 'click-notes-play', expected, deadline, noteEvidence);
  return {
    playDispatched: true,
    noteAcknowledged: true,
    trainerId: current.verdict.trainerId,
    versionFingerprint: current.snapshot.header.trainerButtonVersionFingerprint,
    noteHash
  };
}

async function main() {
  const preflightOnly = process.argv[2] === '--preflight';
  if (preflightOnly) {
    const targets = await getJson('http://127.0.0.1:9222/json/list');
    const selected = selectWandTarget(targets);
    const target = selected.target;
    if (!target) throw new Error(`A unique supported Wand CDP page was not found (${selected.reason})`);
    const client = createCdpClient(target, Date.now() + LAUNCH_TIMEOUT_MS);
    try {
      const result = await inspectPreNavigationState(client);
      console.log(JSON.stringify(result));
      if (!result.ok) process.exitCode = 3;
      return;
    } finally {
      client.close();
    }
  }

  const expected = buildExpectedRegistration(process.argv[2], process.argv[3], process.argv[4], process.argv[5]);
  const targets = await getJson('http://127.0.0.1:9222/json/list');
  const selected = selectWandTarget(targets);
  const target = selected.target;
  if (!target) throw new Error(`A unique supported Wand CDP page was not found (${selected.reason})`);

  const deadline = Date.now() + LAUNCH_TIMEOUT_MS;
  const client = createCdpClient(target, deadline);
  try {
    const nonce = `${Date.now()}-${process.pid}`;
    const navigation = await prepareLaunchNavigation(client, expected, nonce);
    if (!navigation.ok) {
      console.log(JSON.stringify(navigation));
      process.exitCode = 3;
      return;
    }
    const result = await dispatchVerifiedPlay(client, expected, deadline);
    console.log(JSON.stringify({
      navigated: navigation.navigated,
      playDispatched: result.playDispatched,
      dispatchOutcome: result.playDispatched ? 'dispatched' : 'unknown',
      noteAcknowledged: result.noteAcknowledged,
      noteHash: result.noteHash,
      titleId: expected.titleId,
      gameId: expected.gameId,
      trainerId: result.trainerId,
      versionFingerprint: result.versionFingerprint,
      executablePath: expected.executablePath,
      href: navigation.href
    }));
  } finally {
    client.close();
  }
}

if (require.main === module) {
  main().catch((error) => {
    if (process.argv[2] === '--preflight') {
      console.log(JSON.stringify({
        ok: false,
        state: 'unknown',
        navigated: false,
        playDispatched: false,
        reason: 'trainer-state-unavailable-before-navigation'
      }));
      process.exitCode = 3;
      return;
    }
    if (error instanceof SafetyGateError) {
      console.error(error.reason);
      console.log(JSON.stringify({
        navigated: launchNavigationAttempted,
        playDispatched: false,
        blocked: true,
        dispatchOutcome: launchNavigationAttempted ? 'unknown' : 'blocked',
        reason: error.reason
      }));
      process.exitCode = 3;
      return;
    }
    console.error(error && (error.stack || error.message) || String(error));
    console.log(JSON.stringify({
      navigated: launchNavigationAttempted,
      playDispatched: false,
      blocked: false,
      dispatchOutcome: launchNavigationAttempted ? 'unknown' : 'not-dispatched',
      reason: launchNavigationAttempted ? 'launch-outcome-unknown' : 'launch-unavailable'
    }));
    process.exitCode = 1;
  });
}

module.exports = {
  SafetyGateError,
  actionExpression,
  buildExpectedRegistration,
  formatEvaluationFailure,
  inspectPreNavigationState,
  normalizeWindowsPath,
  noteTextHash,
  prepareLaunchNavigation,
  readWandPage,
  readSnapshotExpression,
  requireCanonicalId,
  validateLaunchNavigation,
  validatePreNavigationSnapshot,
  validateLaunchSnapshot
};
