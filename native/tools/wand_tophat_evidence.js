'use strict';

const fs = require('fs');
const path = require('path');
const { TextDecoder } = require('util');

const MAX_TRACE_FILE_BYTES = 16 * 1024 * 1024;
const MAX_FRAME_BYTES = 8 * 1024 * 1024;
const MAX_TRACE_FILES = 32;
const FILETIME_EPOCH_TICKS = 116444736000000000n;
const utf8 = new TextDecoder('utf-8', { fatal: true });

function readVarint(buffer, state) {
  let result = 0n;
  let shift = 0n;
  for (let count = 0; count < 10 && state.offset < buffer.length; count += 1) {
    const byte = buffer[state.offset++];
    result |= BigInt(byte & 0x7f) << shift;
    if ((byte & 0x80) === 0) return result;
    shift += 7n;
  }
  throw new Error('invalid protobuf varint');
}

function parseProtoMessage(buffer) {
  const fields = [];
  const state = { offset: 0 };
  while (state.offset < buffer.length) {
    const key = readVarint(buffer, state);
    const number = Number(key >> 3n);
    const wire = Number(key & 7n);
    if (number <= 0 || number > 0x1fffffff) throw new Error('invalid protobuf field number');
    if (wire === 0) {
      fields.push({ number, wire, value: readVarint(buffer, state) });
      continue;
    }
    if (wire === 1 || wire === 5) {
      const length = wire === 1 ? 8 : 4;
      if (state.offset + length > buffer.length) throw new Error('truncated protobuf fixed-width field');
      fields.push({ number, wire, value: buffer.subarray(state.offset, state.offset + length) });
      state.offset += length;
      continue;
    }
    if (wire === 2) {
      const lengthValue = readVarint(buffer, state);
      if (lengthValue > BigInt(MAX_FRAME_BYTES)) throw new Error('protobuf field exceeds the size limit');
      const length = Number(lengthValue);
      if (state.offset + length > buffer.length) throw new Error('truncated protobuf length-delimited field');
      fields.push({ number, wire, value: buffer.subarray(state.offset, state.offset + length) });
      state.offset += length;
      continue;
    }
    throw new Error('unsupported protobuf wire type');
  }
  return fields;
}

function fieldsOf(message, number, wire) {
  const fields = message.filter((field) => field.number === number);
  if (fields.some((field) => field.wire !== wire)) throw new Error('protobuf field has an unexpected wire type');
  return fields;
}

function oneField(message, number, wire, required = false) {
  const fields = fieldsOf(message, number, wire);
  if (fields.length > 1 || (required && fields.length !== 1)) throw new Error('protobuf field is missing or duplicated');
  return fields[0] || null;
}

function decodeString(buffer) {
  try { return utf8.decode(buffer); }
  catch { throw new Error('protobuf string is not valid UTF-8'); }
}

function readString(message, number, required = false) {
  const field = oneField(message, number, 2, required);
  return field ? decodeString(field.value) : null;
}

function readInteger(message, number, required = false) {
  const field = oneField(message, number, 0, required);
  if (!field) return null;
  if (field.value > BigInt(Number.MAX_SAFE_INTEGER)) throw new Error('protobuf integer is outside the safe range');
  return Number(field.value);
}

function readFixed64(message, number, required = false) {
  const field = oneField(message, number, 1, required);
  return field ? field.value.readBigUInt64LE(0) : null;
}

function parseAnyValue(buffer) {
  const message = parseProtoMessage(buffer);
  const variants = message.filter((field) => field.number >= 1 && field.number <= 7);
  if (variants.length !== 1) throw new Error('OTLP AnyValue is missing or has conflicting variants');
  const field = variants[0];
  if (field.number === 1 && field.wire === 2) return { type: 'string', value: decodeString(field.value) };
  if (field.number === 2 && field.wire === 0) {
    if (field.value !== 0n && field.value !== 1n) throw new Error('OTLP boolean value is invalid');
    return { type: 'boolean', value: field.value === 1n };
  }
  if (field.number === 3 && field.wire === 0) {
    if (field.value > BigInt(Number.MAX_SAFE_INTEGER)) throw new Error('OTLP integer value is outside the safe range');
    return { type: 'integer', value: Number(field.value) };
  }
  if (field.number === 4 && field.wire === 1) return { type: 'double', value: field.value };
  if (field.number === 5 && field.wire === 2) return { type: 'array', value: field.value };
  if (field.number === 6 && field.wire === 2) return { type: 'kvlist', value: field.value };
  if (field.number === 7 && field.wire === 2) return { type: 'bytes', value: field.value };
  throw new Error('OTLP AnyValue variant has an unexpected wire type');
}

function parseAttributes(fields) {
  const attributes = new Map();
  for (const field of fields) {
    const entry = parseProtoMessage(field.value);
    const key = readString(entry, 1, true);
    const valueField = oneField(entry, 2, 2, true);
    if (!key || attributes.has(key)) throw new Error('OTLP attribute key is empty or duplicated');
    attributes.set(key, parseAnyValue(valueField.value));
  }
  return attributes;
}

function getAttribute(attributes, key, type) {
  const value = attributes.get(key);
  if (!value || value.type !== type) throw new Error('OTLP attribute is missing or has the wrong type');
  return value.value;
}

function parseSpan(buffer) {
  const message = parseProtoMessage(buffer);
  const name = readString(message, 5, true);
  const startNs = readFixed64(message, 7, true);
  const endNs = readFixed64(message, 8, true);
  const attributes = parseAttributes(fieldsOf(message, 9, 2));
  if (endNs < startNs) throw new Error('OTLP span end precedes its start');
  return { name, startNs, endNs, attributes };
}

function parseResourceSpans(buffer) {
  const message = parseProtoMessage(buffer);
  const resourceField = oneField(message, 1, 2, true);
  const resource = parseProtoMessage(resourceField.value);
  const resourceAttributes = parseAttributes(fieldsOf(resource, 1, 2));
  const hostProcess = getAttribute(resourceAttributes, 'host.process', 'string');
  const scopeSpans = fieldsOf(message, 2, 2).flatMap((scopeField) => {
    const scopeMessage = parseProtoMessage(scopeField.value);
    return fieldsOf(scopeMessage, 2, 2).map((spanField) => parseSpan(spanField.value));
  });
  return { hostProcess, spans: scopeSpans };
}

function parseExportRequest(buffer) {
  const message = parseProtoMessage(buffer);
  const resourceSpans = fieldsOf(message, 1, 2);
  if (resourceSpans.length === 0) throw new Error('OTLP trace request has no resource spans');
  return resourceSpans.map((field) => parseResourceSpans(field.value));
}

function parseLengthDelimitedFrames(buffer) {
  const frames = [];
  let offset = 0;
  let truncatedTail = false;
  while (offset < buffer.length) {
    if (buffer.length - offset < 4) {
      truncatedTail = true;
      break;
    }
    const length = buffer.readUInt32LE(offset);
    offset += 4;
    if (length === 0 || length > MAX_FRAME_BYTES) throw new Error('OTLP frame length is invalid');
    if (offset + length > buffer.length) {
      truncatedTail = true;
      break;
    }
    frames.push(buffer.subarray(offset, offset + length));
    offset += length;
  }
  return { frames, truncatedTail };
}

function parseFileTime(fileTime) {
  if (typeof fileTime !== 'string' || !/^[0-9A-Fa-f]{16}$/.test(fileTime)) throw new Error('process creation FILETIME is invalid');
  const ticks = BigInt(`0x${fileTime}`);
  if (ticks < FILETIME_EPOCH_TICKS) throw new Error('process creation FILETIME predates the Unix epoch');
  return Number((ticks - FILETIME_EPOCH_TICKS) / 10000n);
}

function parseUtc(value, label) {
  const millis = value instanceof Date ? value.getTime() : Date.parse(value);
  if (!Number.isFinite(millis)) throw new Error(`${label} is invalid`);
  return millis;
}

function parseSessionId(value) {
  const match = /^([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}):([1-9]\d*):([1-9]\d*):1$/i.exec(value);
  if (!match) throw new Error('Tophat session identifier is malformed');
  const pid = Number(match[2]);
  const startMs = Number(match[3]);
  if (!Number.isSafeInteger(pid) || !Number.isSafeInteger(startMs)) throw new Error('Tophat session identity is outside the safe range');
  return { clientId: match[1].toLowerCase(), processId: pid, startMs };
}

function spanIdentity(span, hostProcess, options) {
  const attributes = span.attributes;
  const sessionId = getAttribute(attributes, 'tophat.session_id', 'string');
  const session = parseSessionId(sessionId);
  const clientId = getAttribute(attributes, 'tophat.client_id', 'string');
  if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(clientId)
      || clientId.toLowerCase() !== session.clientId) throw new Error('Tophat session metadata is inconsistent');
  if (session.processId !== options.expectedProcessId) throw new Error('Tophat session PID does not match');
  const expectedName = path.basename(options.expectedProcessName).replace(/\.exe$/i, '').toLowerCase();
  if (!expectedName || hostProcess.toLowerCase() !== expectedName) throw new Error('Tophat host process name does not match');
  if (session.startMs < options.processStartMs - 2000 || session.startMs > options.nowMs + 2000) {
    throw new Error('Tophat session time does not match the process lifetime');
  }
  const startMs = Number(span.startNs / 1_000_000n);
  const endMs = Number(span.endNs / 1_000_000n);
  if (startMs < options.processStartMs - 2000 || endMs > options.nowMs + 2000) {
    throw new Error('Tophat span time does not match the process lifetime');
  }
  return {
    key: `${sessionId}\n${clientId.toLowerCase()}`,
    sessionStartMs: session.startMs,
    startMs,
    endMs,
    attributes
  };
}

function trainerSpanKind(span) {
  const attributes = span.attributes;
  const method = getAttribute(attributes, 'rpc.method', 'string');
  const grpcStatus = getAttribute(attributes, 'rpc.grpc.status_code', 'integer');
  if (grpcStatus !== 0) return null;
  if (span.name === 'LoadPlugin' && method === 'LoadPlugin'
      && getAttribute(attributes, 'plugin.name', 'string') === 'trainerlib') return 'plugin';
  if (span.name === 'ExecuteCommand' && method === 'ExecuteCommand'
      && getAttribute(attributes, 'command.name', 'string') === 'trainer_run_mod_json'
      && getAttribute(attributes, 'command.success', 'boolean') === true
      && getAttribute(attributes, 'command.handled_by', 'string') === 'trainerlib') return 'command';
  return null;
}

function inspectTrainerTraceBuffer(buffer, input) {
  const negative = (reason) => ({ trainerObserved: false, reason });
  try {
    if (!Buffer.isBuffer(buffer) || buffer.length === 0 || buffer.length > MAX_TRACE_FILE_BYTES) return negative('trace-size-invalid');
    if (!input || typeof input !== 'object') return negative('trace-options-invalid');
    const expectedProcessId = input.expectedProcessId;
    if (!Number.isSafeInteger(expectedProcessId) || expectedProcessId <= 0) return negative('trace-options-invalid');
    const expectedProcessName = typeof input.expectedProcessName === 'string' ? input.expectedProcessName.trim() : '';
    if (!expectedProcessName || expectedProcessName.length > 128 || /[\\/\u0000-\u001f]/.test(expectedProcessName)) {
      return negative('trace-options-invalid');
    }
    const processStartMs = parseFileTime(input.processCreationFileTime);
    const attemptMs = parseUtc(input.attemptStartedUtc, 'attempt time');
    const fileLastWriteMs = parseUtc(input.fileLastWriteUtc, 'trace file time');
    const nowMs = input.nowUtc === undefined ? Date.now() : parseUtc(input.nowUtc, 'current time');
    if (attemptMs > nowMs + 2000 || fileLastWriteMs > nowMs + 10000) return negative('trace-time-invalid');
    if (fileLastWriteMs < attemptMs - 1000) return negative('trace-not-fresh');
    const framed = parseLengthDelimitedFrames(buffer);
    if (framed.frames.length === 0) return negative('trace-no-complete-frames');
    const sessions = new Map();
    for (const frame of framed.frames) {
      for (const resource of parseExportRequest(frame)) {
        const expectedName = path.basename(expectedProcessName).replace(/\.exe$/i, '').toLowerCase();
        if (resource.hostProcess.toLowerCase() !== expectedName) continue;
        for (const span of resource.spans) {
          if (!['LoadPlugin', 'ExecuteCommand'].includes(span.name)) continue;
          const kind = trainerSpanKind(span);
          if (!kind) continue;
          let identity;
          try {
            identity = spanIdentity(span, resource.hostProcess, {
              expectedProcessName,
              expectedProcessId,
              processStartMs,
              nowMs
            });
          } catch (error) {
            if (error && ['Tophat session PID does not match', 'Tophat session time does not match the process lifetime',
              'Tophat span time does not match the process lifetime'].includes(error.message)) continue;
            throw error;
          }
          const session = sessions.get(identity.key) || { plugin: null, commands: [] };
          if (kind === 'plugin') {
            if (!session.plugin || identity.endMs > session.plugin.endMs) session.plugin = identity;
          } else {
            session.commands.push(identity);
          }
          sessions.set(identity.key, session);
        }
      }
    }
    const fresh = [];
    for (const session of sessions.values()) {
      if (!session.plugin) continue;
      for (const command of session.commands) {
        if (command.startMs < attemptMs - 1000 || command.endMs < attemptMs - 1000) continue;
        if (session.plugin.startMs > command.startMs || session.plugin.endMs > command.endMs) continue;
        fresh.push({ command, plugin: session.plugin });
      }
    }
    if (fresh.length === 0) return negative(framed.truncatedTail ? 'trace-evidence-incomplete' : 'no-fresh-trainer-command');
    fresh.sort((left, right) => right.command.endMs - left.command.endMs);
    const evidence = fresh[0];
    return {
      trainerObserved: true,
      reason: 'trainer-command-success',
      processName: path.basename(expectedProcessName).replace(/\.exe$/i, ''),
      processId: expectedProcessId,
      observedUtc: new Date(evidence.command.endMs).toISOString(),
      sessionStartedUtc: new Date(evidence.command.sessionStartMs).toISOString(),
      pluginLoadedUtc: new Date(evidence.plugin.endMs).toISOString()
    };
  } catch (error) {
    return negative(error && error.message === 'Tophat session PID does not match'
      ? 'different-process'
      : error && error.message === 'Tophat host process name does not match'
        ? 'different-process-name'
        : 'malformed-or-unmatched-trace');
  }
}

function inspectTrainerTraceDirectory(directory, input, dependencies = {}) {
  const readFile = dependencies.readFileSync || fs.readFileSync;
  const stat = dependencies.statSync || fs.statSync;
  const readdir = dependencies.readdirSync || fs.readdirSync;
  const clock = dependencies.clock || (() => new Date());
  const nowUtc = input && input.nowUtc ? input.nowUtc : clock();
  const negative = (reason) => ({ trainerObserved: false, reason });
  if (typeof directory !== 'string' || !directory) return negative('trace-directory-invalid');
  let files;
  try {
    files = readdir(directory).filter((name) => typeof name === 'string' && name.endsWith('.traces.otlp'));
  } catch (error) {
    return negative(error && error.code === 'ENOENT' ? 'trace-directory-not-found' : 'trace-directory-unavailable');
  }
  const candidates = [];
  for (const name of files) {
    const fullPath = path.join(directory, name);
    try {
      const info = stat(fullPath);
      if (!info.isFile() || info.size <= 0 || info.size > MAX_TRACE_FILE_BYTES) continue;
      candidates.push({ name, fullPath, info });
    } catch { /* An incomplete or concurrently replaced trace is ignored. */ }
  }
  candidates.sort((left, right) => right.info.mtimeMs - left.info.mtimeMs);
  const lastWriteUtc = input && input.attemptStartedUtc ? Date.parse(input.attemptStartedUtc) : NaN;
  for (const candidate of candidates.slice(0, MAX_TRACE_FILES)) {
    if (Number.isFinite(lastWriteUtc) && candidate.info.mtimeMs < lastWriteUtc - 1000) continue;
    let buffer;
    try { buffer = readFile(candidate.fullPath); }
    catch { continue; }
    const result = inspectTrainerTraceBuffer(buffer, {
      ...input,
      fileLastWriteUtc: candidate.info.mtime,
      nowUtc
    });
    if (result.trainerObserved) return { ...result, traceFileName: candidate.name };
  }
  return negative('no-fresh-trainer-trace');
}

module.exports = {
  inspectTrainerTraceBuffer,
  inspectTrainerTraceDirectory,
  parseLengthDelimitedFrames
};
