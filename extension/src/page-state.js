// What the page is doing, as far as filtering cares: which video is being
// watched, whether the player is showing an ad, whether the <video> element
// has been found, and whether a navigation is under way. Pure.
//
// page.js turns browser signals into the events below; everything else
// (the Playback Gate, Live Censoring, the heartbeat) reads the state.
//
// Phases:
// - idle: not a watch page (or the page is going away). Nothing to filter.
// - leaving: a navigation has started (`yt-navigate-start`). Per ADR-0007,
//   `location` already shows the next video while the element still plays
//   the old one, so nothing is known about what is audible: mute, drop the
//   snapshot, and wait. `videoId` is null.
// - watching: a watch page for `videoId` has finished loading
//   (`yt-navigate-finish`, or the content script started on one).
//
// Decided (W12):
// - `generation` rises by one every time `watching` begins: from idle, from
//   leaving, or straight from watching another video. It rises too for a
//   navigation that ends on the same video (e.g. a `&t=` link): that passed
//   through `leaving`, which dropped the snapshot and may have reloaded the
//   element, so it is a new watching period like any other and older replies
//   are discarded (at worst one heartbeat is wasted). The only thing that
//   keeps the generation is a `navigate-finish`/`initial` for the video
//   already being watched with no `navigate-start` in between, which changes
//   nothing at all.
// - `navigate-start` leads to `leaving` from every phase, idle included: the
//   miniplayer keeps playing on non-watch pages, and muting nothing is free.
// - `adShowing` and `hasVideoElement` describe the player, not the video, so
//   they are kept through every phase change, pagehide included.
// - A malformed event (unknown type, missing or mistyped field) changes
//   nothing.

import { videoIdFromUrl } from './video-id.js';

/**
 * @typedef {'idle' | 'leaving' | 'watching'} Phase
 *
 * @typedef {object} PageState
 * @property {Phase} phase
 * @property {string | null} videoId  The video being watched; set exactly when `phase` is `watching`.
 * @property {boolean} adShowing  `#movie_player` has the `ad-showing` class.
 * @property {boolean} hasVideoElement  `video.html5-main-video` has been found and is in the document.
 * @property {number} generation  Rises each time a watching period begins; 0 before the first.
 *
 * @typedef {{ type: 'navigate-start' }
 *   | { type: 'navigate-finish', url: string }
 *   | { type: 'initial', url: string }
 *   | { type: 'video-element', present: boolean }
 *   | { type: 'ad', showing: boolean }
 *   | { type: 'pagehide' }} PageEvent
 *
 * @callback PageStateListener
 * @param {PageState} state  The new state.
 * @param {PageState} previous  The state before the event.
 * @param {PageEvent} event  The event that changed it.
 * @returns {void}
 */

/** @type {readonly Phase[]} */
export const PHASES = Object.freeze(['idle', 'leaving', 'watching']);

/** Before the content script has looked at anything. @type {Readonly<PageState>} */
export const INITIAL_PAGE_STATE = Object.freeze({
  phase: 'idle',
  videoId: null,
  adShowing: false,
  hasVideoElement: false,
  generation: 0,
});

/**
 * The state after one event. Returns `state` itself (the same object) when
 * nothing changed, and a new frozen object otherwise; never mutates.
 *
 * @param {PageState} state
 * @param {PageEvent | unknown} event
 * @returns {PageState}
 */
export function reducePageState(state, event) {
  switch (/** @type {any} */ (event)?.type) {
    case 'navigate-start':
      return with_(state, { phase: 'leaving', videoId: null });

    case 'navigate-finish':
    case 'initial': {
      const url = /** @type {any} */ (event).url;
      if (typeof url !== 'string') {
        return state;
      }
      return arriveAt(state, videoIdFromUrl(url));
    }

    case 'video-element': {
      const present = /** @type {any} */ (event).present;
      return typeof present === 'boolean' ? with_(state, { hasVideoElement: present }) : state;
    }

    case 'ad': {
      const showing = /** @type {any} */ (event).showing;
      return typeof showing === 'boolean' ? with_(state, { adShowing: showing }) : state;
    }

    case 'pagehide':
      return with_(state, { phase: 'idle', videoId: null });

    default:
      return state;
  }
}

/**
 * A page finished loading at a URL whose video is `videoId` (null: not a
 * watch page).
 *
 * @param {PageState} state
 * @param {string | null} videoId
 * @returns {PageState}
 */
function arriveAt(state, videoId) {
  if (videoId === null) {
    return with_(state, { phase: 'idle', videoId: null });
  }
  if (state.phase === 'watching' && state.videoId === videoId) {
    return state;
  }
  return with_(state, { phase: 'watching', videoId, generation: state.generation + 1 });
}

/**
 * `state` with `changes` applied, or `state` itself if they change nothing.
 *
 * @param {PageState} state
 * @param {Partial<PageState>} changes
 * @returns {PageState}
 */
function with_(state, changes) {
  const keys = /** @type {(keyof PageState)[]} */ (Object.keys(changes));
  if (keys.every((key) => Object.is(state[key], changes[key]))) {
    return state;
  }
  return Object.freeze({ ...state, ...changes });
}

/**
 * Whether the programme audio must be held silent regardless of Hits: a
 * navigation is under way and what the element plays is unknown.
 *
 * @param {PageState} state
 * @returns {boolean}
 */
export function shouldMute(state) {
  return state.phase === 'leaving';
}

/**
 * Whether the page is showing a video that can be filtered now: watching,
 * no ad, and the element found. The Playback Gate and Live Censoring act
 * only when this is true (and stand aside during an ad).
 *
 * @param {PageState} state
 * @returns {boolean}
 */
export function isFilterable(state) {
  return state.phase === 'watching' && !state.adShowing && state.hasVideoElement;
}

/**
 * Holds the current state and tells `onChange` about every real change.
 * Events that change nothing are not reported.
 *
 * @param {object} [options]
 * @param {PageStateListener} [options.onChange]  Called synchronously, after the state is updated.
 * @param {PageState} [options.initial]  Defaults to INITIAL_PAGE_STATE.
 * @returns {{ readonly state: PageState, dispatch(event: PageEvent): boolean }}
 *   `dispatch` returns whether the state changed.
 */
export function createPageState({ onChange, initial = INITIAL_PAGE_STATE } = {}) {
  let state = initial;
  return {
    get state() {
      return state;
    },
    dispatch(event) {
      const previous = state;
      const next = reducePageState(previous, event);
      if (next === previous) {
        return false;
      }
      state = next;
      onChange?.(next, previous, event);
      return true;
    },
  };
}
