// The messages a content script (or the options page) sends the service
// worker, and the replies it gets back. Pure.
//
// Why a relay at all: a content script's fetch runs with the page's origin, so
// youtube.com cannot call the local service (no CORS policy, by design - W10).
// The service worker's fetch is covered by the extension's host_permissions.
//
// Every message is a plain object with a `type` starting `ff/`. Every reply is
// an api-client result: `{ ok: true, ... }` or `{ ok: false, error: { kind, ... } }`.

/**
 * @typedef {import('./api-client.js').ApiError} ApiError
 * @typedef {import('./api-client.js').Failure} Failure
 *
 * @typedef {{ type: 'ff/heartbeat', provider: string, videoId: string, position: number, since: number | null, session: string | null }} HeartbeatMessage
 * @typedef {{ type: 'ff/get', provider: string, videoId: string, since: number | null, session: string | null }} GetMessage
 * @typedef {{ type: 'ff/cancel', provider: string, videoId: string }} CancelMessage
 * @typedef {{ type: 'ff/config', serverUrl: string | null }} ConfigMessage
 * @typedef {HeartbeatMessage | GetMessage | CancelMessage | ConfigMessage} Message
 *
 * @typedef {{ ok: true, [key: string]: unknown } | Failure} Reply
 */

export const MessageType = Object.freeze({
  HEARTBEAT: /** @type {'ff/heartbeat'} */ ('ff/heartbeat'),
  GET: /** @type {'ff/get'} */ ('ff/get'),
  CANCEL: /** @type {'ff/cancel'} */ ('ff/cancel'),
  CONFIG: /** @type {'ff/config'} */ ('ff/config'),
});

const TYPES = new Set(Object.values(MessageType));

/**
 * The failure kind for trouble between the page and the service worker (the
 * extension was reloaded, the worker could not answer) rather than between
 * the worker and the service. W15 treats it as "reload the page".
 */
export const EXTENSION_ERROR = 'extension';

/**
 * @param {{ provider: string, videoId: string, position: number, since?: number | null, session?: string | null }} args
 * @returns {HeartbeatMessage}
 */
export function heartbeatMessage({ provider, videoId, position, since = null, session = null }) {
  return { type: MessageType.HEARTBEAT, provider, videoId, position, since, session };
}

/**
 * @param {{ provider: string, videoId: string, since?: number | null, session?: string | null }} args
 * @returns {GetMessage}
 */
export function getMessage({ provider, videoId, since = null, session = null }) {
  return { type: MessageType.GET, provider, videoId, since, session };
}

/**
 * @param {{ provider: string, videoId: string }} args
 * @returns {CancelMessage}
 */
export function cancelMessage({ provider, videoId }) {
  return { type: MessageType.CANCEL, provider, videoId };
}

/**
 * @param {{ serverUrl?: string | null }} [args]  Test this URL instead of the saved one (the options page's
 *   "Test connection" before saving).
 * @returns {ConfigMessage}
 */
export function configMessage({ serverUrl = null } = {}) {
  return { type: MessageType.CONFIG, serverUrl };
}

/**
 * Whether a runtime message is addressed to this protocol at all. The worker
 * leaves anything else alone.
 *
 * @param {unknown} raw
 */
export function isProtocolMessage(raw) {
  return isObject(raw) && typeof raw.type === 'string' && raw.type.startsWith('ff/');
}

/**
 * Whether a message came from this extension (its content scripts or pages).
 * Web pages cannot reach `runtime.onMessage` without `externally_connectable`,
 * which the manifest does not declare, so this is belt and braces.
 *
 * @param {unknown} sender  A `chrome.runtime.MessageSender`.
 * @param {string} extensionId  `chrome.runtime.id`.
 */
export function isFromExtension(sender, extensionId) {
  return isObject(sender) && typeof extensionId === 'string' && extensionId !== '' && sender.id === extensionId;
}

/**
 * Check a message's shape and copy out only its known fields. Values are
 * checked for type here; the api-client checks their content (ID format,
 * finite position) before anything is sent.
 *
 * @param {unknown} raw
 * @returns {{ ok: true, message: Message } | Failure}
 */
export function parseMessage(raw) {
  if (!isObject(raw)) {
    return invalid('A message must be an object.');
  }
  if (typeof raw.type !== 'string' || !TYPES.has(/** @type {any} */ (raw.type))) {
    return invalid(`Unknown message type ${JSON.stringify(raw.type)}.`);
  }

  if (raw.type === MessageType.CONFIG) {
    if (!isOptional(raw.serverUrl, 'string')) {
      return invalid('serverUrl must be a string or null.');
    }
    return { ok: true, message: configMessage({ serverUrl: raw.serverUrl ?? null }) };
  }

  if (typeof raw.provider !== 'string' || typeof raw.videoId !== 'string') {
    return invalid('provider and videoId must be strings.');
  }
  if (raw.type === MessageType.CANCEL) {
    return { ok: true, message: cancelMessage({ provider: raw.provider, videoId: raw.videoId }) };
  }

  if (!isOptional(raw.since, 'number') || !isOptional(raw.session, 'string')) {
    return invalid('since must be a number or null, and session a string or null.');
  }
  const read = { provider: raw.provider, videoId: raw.videoId, since: raw.since ?? null, session: raw.session ?? null };
  if (raw.type === MessageType.GET) {
    return { ok: true, message: getMessage(read) };
  }

  if (typeof raw.position !== 'number') {
    return invalid('position must be a number.');
  }
  return { ok: true, message: heartbeatMessage({ ...read, position: raw.position }) };
}

/**
 * @param {string} kind
 * @param {string} detail
 * @returns {Failure}
 */
export function failureReply(kind, detail) {
  return { ok: false, error: /** @type {ApiError} */ ({ kind, detail }) };
}

/**
 * Whether a reply has the protocol's outer shape.
 *
 * @param {unknown} raw
 * @returns {raw is Reply}
 */
export function isReply(raw) {
  if (!isObject(raw)) return false;
  if (raw.ok === true) return true;
  return raw.ok === false && isObject(raw.error) && typeof raw.error.kind === 'string';
}

/**
 * Wrap `chrome.runtime.sendMessage` so a request always resolves to a reply:
 * a rejection (extension reloaded, no worker listening) or a malformed
 * answer becomes an `extension` failure rather than an exception.
 *
 * @param {(message: Message) => Promise<unknown>} sendMessage
 * @returns {(message: Message) => Promise<Reply>}
 */
export function createMessenger(sendMessage) {
  return async (message) => {
    let reply;
    try {
      reply = await sendMessage(message);
    } catch (error) {
      const detail = isObject(error) && typeof error.message === 'string' ? error.message : String(error);
      return failureReply(EXTENSION_ERROR, `The extension did not answer: ${detail}`);
    }
    return isReply(reply)
      ? reply
      : failureReply(EXTENSION_ERROR, 'The extension gave no usable answer. Reload the page.');
  };
}

/** @param {string} detail */
function invalid(detail) {
  return failureReply('invalid_request', detail);
}

/**
 * @param {unknown} value
 * @param {'string' | 'number'} type
 */
function isOptional(value, type) {
  return value === undefined || value === null || typeof value === type;
}

/**
 * @param {unknown} value
 * @returns {value is Record<string, any>}
 */
function isObject(value) {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
