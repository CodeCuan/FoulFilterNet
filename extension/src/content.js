// The content script's entry point, loaded as an ES module by
// content-loader.js. It may import any module under src/, and talks to the
// service only through the service worker (protocol.js), never by fetch.
//
// W15: hands the real browser to content-app.js, which ties the page watcher,
// the heartbeat, the Playback Gate and Live Censoring together (the
// decisions are session.js's).
//
// Debugging: `localStorage.ffDebug = '1'` in YouTube's console (the page's
// own storage, which the content script shares) logs every session event.

import { getPageAudio } from './audio-graph.js';
import { startContent } from './content-app.js';
import { createMessenger } from './protocol.js';
import { createSettingsStore } from './settings.js';

/** Whether session events are logged to the console. */
function debugEnabled() {
  try {
    return globalThis.localStorage?.getItem('ffDebug') === '1';
  } catch {
    return false;
  }
}

const app = startContent({
  document,
  window,
  send: createMessenger((message) => chrome.runtime.sendMessage(message)),
  settings: createSettingsStore({ area: chrome.storage.sync, onChanged: chrome.storage.onChanged }),
  audio: getPageAudio(),
  log: debugEnabled() ? (what, detail) => console.debug('FoulFilter', what, detail) : undefined,
});

/**
 * The page's latest state.
 *
 * @returns {import('./page-state.js').PageState}
 */
export function currentPageState() {
  return app.watcher.state;
}

/** The page watcher itself (its `video` element and `stop`). */
export const watcher = app.watcher;

/** The running wiring (its session `state` and last `derived` outputs), for debugging. */
export { app };
