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
 * @typedef {import('./protocol.js').BadgeMessage} BadgeMessage
 *
 * @typedef {object} ActionLike  The parts of `chrome.action` used for the badge.
 * @property {(details: { tabId: number, text: string }) => Promise<void> | void} setBadgeText
 * @property {(details: { tabId: number, color: string }) => Promise<void> | void} setBadgeBackgroundColor
 * @property {(details: { tabId: number, title: string }) => Promise<void> | void} setTitle
 */

/**
 * @param {object} dependencies
 * @param {() => Promise<Settings>} dependencies.loadSettings
 * @param {FetchLike} dependencies.fetch
 * @param {number} [dependencies.timeoutMs]
 * @param {typeof setTimeout} [dependencies.setTimeout]
 * @param {typeof clearTimeout} [dependencies.clearTimeout]
 * @param {ActionLike} [dependencies.action]  `chrome.action`, for `ff/badge`.
 * @returns {{ handle: (raw: unknown, sender?: unknown) => Promise<Reply> }}
 */
export function createRelay({ loadSettings, fetch, timeoutMs = DEFAULT_TIMEOUT_MS, setTimeout, clearTimeout, action }) {
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
   * Show a content script's badge on its own tab. Only a message from a tab
   * has one; the options page cannot set a badge.
   *
   * @param {BadgeMessage} message
   * @param {unknown} sender
   * @returns {Promise<Reply>}
   */
  async function showBadge(message, sender) {
    const tabId = /** @type {any} */ (sender)?.tab?.id;
    if (!Number.isSafeInteger(tabId) || tabId < 0) {
      return failureReply('invalid_request', 'A badge can only be set from a tab.');
    }
    if (!action) {
      return failureReply(EXTENSION_ERROR, 'The toolbar button is not available.');
    }
    await Promise.all([
      action.setBadgeText({ tabId, text: message.text }),
      action.setBadgeBackgroundColor({ tabId, color: message.color }),
      action.setTitle({ tabId, title: message.title }),
    ]);
    return { ok: true };
  }

  /**
   * @param {unknown} raw
   * @param {unknown} [sender]  The `chrome.runtime.MessageSender`; needed for `ff/badge`.
   * @returns {Promise<Reply>}
   */
  async function handle(raw, sender) {
    const parsed = parseMessage(raw);
    if (!parsed.ok) {
      return parsed;
    }
    const message = parsed.message;

    try {
      if (message.type === MessageType.BADGE) {
        return await showBadge(message, sender);
      }

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
