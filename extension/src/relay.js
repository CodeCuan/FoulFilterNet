// What the service worker does with a message: read the settings, make one
// API call, hand back the result. Stateless - an MV3 worker is stopped when
// idle, so nothing may live between messages. Pure apart from what it is given.

import { createApiClient, DEFAULT_TIMEOUT_MS } from './api-client.js';
import { EXTENSION_ERROR, MessageType, failureReply, parseMessage } from './protocol.js';
import { DEFAULT_SETTINGS, normaliseServerUrl } from './settings.js';

/**
 * @typedef {import('./settings.js').Settings} Settings
 * @typedef {import('./protocol.js').Reply} Reply
 * @typedef {import('./api-client.js').FetchLike} FetchLike
 */

/**
 * @param {object} dependencies
 * @param {() => Promise<Settings>} dependencies.loadSettings
 * @param {FetchLike} dependencies.fetch
 * @param {number} [dependencies.timeoutMs]
 * @param {typeof setTimeout} [dependencies.setTimeout]
 * @param {typeof clearTimeout} [dependencies.clearTimeout]
 * @returns {{ handle: (raw: unknown) => Promise<Reply> }}
 */
export function createRelay({ loadSettings, fetch, timeoutMs = DEFAULT_TIMEOUT_MS, setTimeout, clearTimeout }) {
  /** @param {string} serverUrl */
  const clientFor = (serverUrl) => createApiClient({ serverUrl, fetch, timeoutMs, setTimeout, clearTimeout });

  /** @returns {Promise<Settings>} */
  async function settings() {
    try {
      return await loadSettings();
    } catch {
      return { ...DEFAULT_SETTINGS };
    }
  }

  /**
   * @param {unknown} raw
   * @returns {Promise<Reply>}
   */
  async function handle(raw) {
    const parsed = parseMessage(raw);
    if (!parsed.ok) {
      return parsed;
    }
    const message = parsed.message;

    try {
      if (message.type === MessageType.CONFIG && message.serverUrl !== null) {
        const url = normaliseServerUrl(message.serverUrl);
        if (!url.ok) {
          return failureReply('invalid_request', url.error);
        }
        return await clientFor(url.url).config();
      }

      const api = clientFor((await settings()).serverUrl);
      switch (message.type) {
        case MessageType.HEARTBEAT:
          return await api.heartbeat(message);
        case MessageType.GET:
          return await api.get(message);
        case MessageType.CANCEL:
          return await api.cancel(message);
        case MessageType.CONFIG:
          return await api.config();
      }
    } catch (error) {
      // The api-client never throws; this is a last line so a bug cannot
      // leave the page waiting on a reply that never comes.
      return failureReply(EXTENSION_ERROR, `The extension failed: ${error instanceof Error ? error.message : String(error)}`);
    }
    return failureReply('invalid_request', 'Unhandled message.');
  }

  return { handle };
}
