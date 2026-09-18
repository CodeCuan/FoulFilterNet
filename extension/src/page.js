// The DOM adapter for page-state.js: turns YouTube's page into page events.
//
// - Navigation: YouTube's own `yt-navigate-start` / `yt-navigate-finish` on
//   the document. `location` is read when the content script starts and at
//   each navigate-finish, never polled (ADR-0007: it shows the next video
//   before the old one stops).
// - The element: `video.html5-main-video`. YouTube reuses it across
//   navigations (ADR-0007), so once found it is kept, and only looked for
//   again if a navigation finds it has left the document. A MutationObserver
//   on the document runs only while the element or the player is missing.
// - Ads: the `ad-showing` class on `#movie_player`, watched with a
//   MutationObserver on that element's `class` attribute.
// - `pagehide` ends watching; a `pageshow` from the back/forward cache starts
//   again as if the content script had just loaded.
//
// Thin on purpose: every decision is in page-state.js. The document, window
// and MutationObserver are handed in so a small fake can drive it in tests.

import { createPageState } from './page-state.js';

/** @typedef {import('./page-state.js').PageState} PageState */
/** @typedef {import('./page-state.js').PageStateListener} PageStateListener */

/** YouTube's main player element; reused across in-app navigations. */
export const VIDEO_SELECTOR = 'video.html5-main-video';

/** The player container whose class list says whether an ad is showing. */
export const PLAYER_SELECTOR = '#movie_player';

/** Set on the player while an ad plays. */
export const AD_CLASS = 'ad-showing';

/**
 * @typedef {object} PageWatcher
 * @property {PageState} state  The latest page state.
 * @property {HTMLVideoElement | null} video  The player's element once found. Whether it is still in the
 *   document is rechecked at each navigate-finish, so a user should also check `isConnected`.
 * @property {() => void} stop  Removes every listener and observer. Idempotent; no change is reported afterwards.
 */

/**
 * Start watching the page. `onChange` may be called before this returns
 * (a watch page is reported at once), so it should use its arguments rather
 * than the returned watcher.
 *
 * @param {object} options
 * @param {Document} options.document
 * @param {Window} options.window  For `location` and `pagehide`/`pageshow`.
 * @param {PageStateListener} [options.onChange]
 * @param {typeof MutationObserver} [options.MutationObserver]  Defaults to `window.MutationObserver`.
 * @returns {PageWatcher}
 */
export function watchPage({ document, window, onChange, MutationObserver = window.MutationObserver }) {
  const machine = createPageState({ onChange });

  /** @type {HTMLVideoElement | null} */
  let video = null;
  /** @type {Element | null} */
  let player = null;
  /** @type {MutationObserver | null} Looks for the video and player while either is missing. */
  let finder = null;
  /** @type {MutationObserver | null} Watches the player's class list. */
  let adWatcher = null;
  let stopped = false;

  /** @param {import('./page-state.js').PageEvent} event */
  const dispatch = (event) => {
    if (!stopped) {
      machine.dispatch(event);
    }
  };

  const readAd = () => {
    if (player !== null) {
      dispatch({ type: 'ad', showing: player.classList.contains(AD_CLASS) });
    }
  };

  // Forget elements that left the document, pick up ones that arrived, and
  // keep the finder running exactly while something is still missing.
  const lookForElements = () => {
    if (stopped) {
      return;
    }
    if (video !== null && !video.isConnected) {
      video = null;
      dispatch({ type: 'video-element', present: false });
    }
    if (player !== null && !player.isConnected) {
      adWatcher?.disconnect();
      adWatcher = null;
      player = null;
      dispatch({ type: 'ad', showing: false });
    }

    if (video === null) {
      video = /** @type {HTMLVideoElement | null} */ (document.querySelector(VIDEO_SELECTOR));
      if (video !== null) {
        dispatch({ type: 'video-element', present: true });
      }
    }
    if (player === null) {
      player = document.querySelector(PLAYER_SELECTOR);
      if (player !== null) {
        adWatcher = new MutationObserver(readAd);
        adWatcher.observe(player, { attributes: true, attributeFilter: ['class'] });
        readAd();
      }
    }

    const missing = video === null || player === null;
    if (!missing && finder !== null) {
      finder.disconnect();
      finder = null;
    } else if (missing && finder === null) {
      finder = new MutationObserver(lookForElements);
      finder.observe(document, { childList: true, subtree: true });
    }
  };

  const here = () => window.location.href;

  const onNavigateStart = () => dispatch({ type: 'navigate-start' });
  const onNavigateFinish = () => {
    lookForElements();
    dispatch({ type: 'navigate-finish', url: here() });
  };
  const onPageHide = () => dispatch({ type: 'pagehide' });
  /** @param {Event} event */
  const onPageShow = (event) => {
    if (/** @type {PageTransitionEvent} */ (event).persisted) {
      lookForElements();
      dispatch({ type: 'initial', url: here() });
    }
  };

  document.addEventListener('yt-navigate-start', onNavigateStart);
  document.addEventListener('yt-navigate-finish', onNavigateFinish);
  window.addEventListener('pagehide', onPageHide);
  window.addEventListener('pageshow', onPageShow);

  lookForElements();
  dispatch({ type: 'initial', url: here() });

  return {
    get state() {
      return machine.state;
    },
    get video() {
      return video;
    },
    stop() {
      if (stopped) {
        return;
      }
      stopped = true;
      document.removeEventListener('yt-navigate-start', onNavigateStart);
      document.removeEventListener('yt-navigate-finish', onNavigateFinish);
      window.removeEventListener('pagehide', onPageHide);
      window.removeEventListener('pageshow', onPageShow);
      finder?.disconnect();
      finder = null;
      adWatcher?.disconnect();
      adWatcher = null;
    },
  };
}
