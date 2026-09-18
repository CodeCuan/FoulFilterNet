// The content script's entry point, loaded as an ES module by
// content-loader.js. It may import any module under src/, and talks to the
// service only through the service worker (protocol.js), never by fetch.
//
// W12 starts the page watcher and keeps its latest state; W15 wires the
// state to the heartbeat, the Playback Gate and Live Censoring.
//
// Debugging: `localStorage.ffDebug = '1'` in YouTube's console (the page's
// own storage, which the content script shares) logs every page state change.

import { watchPage } from './page.js';

/** Whether page state changes are logged to the console. */
function debugEnabled() {
  try {
    return globalThis.localStorage?.getItem('ffDebug') === '1';
  } catch {
    return false;
  }
}

const debug = debugEnabled();

const watcher = watchPage({
  document,
  window,
  onChange(state, _previous, event) {
    if (debug) {
      console.debug('FoulFilter page:', event.type, state);
    }
  },
});

/**
 * The page's latest state, for W15's wiring.
 *
 * @returns {import('./page-state.js').PageState}
 */
export function currentPageState() {
  return watcher.state;
}

/** The page watcher itself (its `video` element and `stop`). */
export { watcher };
