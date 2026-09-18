// The content script's wiring (W15): the page watcher (W12), the heartbeat
// through the service worker (W11), the Playback Gate and its overlay (W13),
// Live Censoring (W14) and the settings, tied together. Adapter: every
// decision is session.js's; this only carries events in and applies what it
// derives. Every browser dependency is handed in, so a test can run a whole
// scenario with fakes; content.js passes the real ones.
//
// Shape:
// - One SessionState, changed only by `dispatch(event)`, after which `sync()`
//   derives the outputs and applies them: censor mode, Hits, method and
//   offset to the censor controller; the gate decision to the gate
//   controller; the overlay model to the overlay; the badge to the worker
//   (only when it changed); and the heartbeat timer.
// - Per <video> element (the watcher's, while connected): one gate
//   controller at once, and, once the audio graph is attached, one censor
//   controller. When the element changes both are stopped (the old gain is
//   muted) and new ones made; the audio state goes back to `detached`.
// - The audio graph is attached on the element's first `play` event (or at
//   once if it is already playing, or from an overlay click), only while
//   enabled and on a watch page, and `resume()`d there. A refusal is the
//   gate's `{kind: 'audio'}` error.
// - The heartbeat: one timer, set for `nextHeartbeatAt`. When it fires, a
//   `tick` may give up a lost heartbeat, then a heartbeat is sent if due,
//   tagged with the generation and sequence number its reply must match.
// - "Watch unfiltered" dispatches `unfiltered`, resumes the context and
//   calls `video.play()` inside the click, a user gesture (the gate does not
//   play a video the user paused). "Enable sound" attaches or resumes the
//   graph and presses play; the gate controller catches that play under its
//   hold and plays again once the context runs.
// - `sync()` is not re-entered: an event raised while applying (a fake
//   element fires `pause` at once) runs one more pass afterwards.

import { createCensorController } from './censor-controller.js';
import { createGateController } from './gate-controller.js';
import { createOverlay } from './overlay.js';
import { PLAYER_SELECTOR, watchPage } from './page.js';
import { badgeMessage, heartbeatMessage } from './protocol.js';
import {
  INITIAL_SESSION,
  deriveSession,
  heartbeatDue,
  heartbeatRequest,
  nextHeartbeatAt,
  reduceSession,
} from './session.js';

/**
 * @typedef {import('./session.js').SessionState} SessionState
 * @typedef {import('./session.js').Media} Media
 * @typedef {import('./session.js').Derived} Derived
 * @typedef {import('./protocol.js').Message} Message
 * @typedef {import('./protocol.js').Reply} Reply
 * @typedef {import('./audio-graph.js').AttachResult} AttachResult
 * @typedef {import('./audio-graph.js').AudioGraph} AudioGraph
 * @typedef {import('./settings.js').Settings} Settings
 *
 * @typedef {object} SettingsSource
 * @property {() => Promise<Settings>} load
 * @property {(listener: (next: Settings) => void) => () => void} subscribe
 *
 * @typedef {object} Bound  What is attached to one <video>.
 * @property {HTMLVideoElement} video
 * @property {ReturnType<typeof createGateController>} gate
 * @property {ReturnType<typeof createCensorController> | null} censor
 * @property {AudioGraph | null} graph
 * @property {() => void} onMedia
 * @property {() => void} onPlay
 * @property {unknown} hits  The Hits last given to the censor controller.
 * @property {string | null} method  The method last given to it.
 * @property {number | null} offsetMs  The offset last given to it.
 * @property {string | null} mode  The mode last given to it.
 */

/** Media events after which the gate decides again. `play` has its own listener. */
export const GATE_EVENTS = Object.freeze([
  'pause',
  'playing',
  'seeked',
  'ratechange',
  'durationchange',
  'loadedmetadata',
  'timeupdate',
  'ended',
  'emptied',
]);

/**
 * Start filtering the page.
 *
 * @param {object} options
 * @param {Document} options.document
 * @param {Window} options.window
 * @param {(message: Message) => Promise<Reply>} options.send  protocol.js's `createMessenger(...)`.
 * @param {SettingsSource} options.settings
 * @param {{ attach(video: object): AttachResult }} options.audio  audio-graph.js's `getPageAudio()`.
 * @param {typeof watchPage} [options.watchPage]
 * @param {typeof createOverlay} [options.createOverlay]
 * @param {typeof MutationObserver} [options.MutationObserver]
 * @param {() => number} [options.now]  Milliseconds.
 * @param {(fn: () => void, ms: number) => unknown} [options.setTimeout]
 * @param {(id: unknown) => void} [options.clearTimeout]
 * @param {(fn: () => void, ms: number) => unknown} [options.setInterval]  For the censor controller's tick.
 * @param {(id: unknown) => void} [options.clearInterval]
 * @param {(what: string, detail: unknown) => void} [options.log]  Debug logging.
 */
export function startContent({
  document,
  window,
  send,
  settings,
  audio,
  watchPage: watch = watchPage,
  createOverlay: makeOverlay = createOverlay,
  MutationObserver,
  now = Date.now,
  setTimeout = globalThis.setTimeout,
  clearTimeout = globalThis.clearTimeout,
  setInterval = globalThis.setInterval,
  clearInterval = globalThis.clearInterval,
  log,
}) {
  /** @type {SessionState} */
  let state = INITIAL_SESSION;
  /** @type {Bound | null} */
  let bound = null;
  /** @type {Derived | null} */
  let derived = null;
  /** @type {string | null} */
  let lastBadge = null;
  /** @type {unknown} */
  let timer = null;
  /** @type {number | null} */
  let timerAt = null;
  let started = false;
  let stopped = false;
  let syncing = false;
  let again = false;

  /** @param {unknown} event */
  function dispatch(event) {
    if (stopped) return;
    const next = reduceSession(state, event);
    if (next === state) return;
    state = next;
    log?.('event', event);
    sync();
  }

  const overlay = makeOverlay({ document, onUnfiltered: clickUnfiltered, onEnable: clickEnable });

  const watcher = watch({
    document,
    window,
    ...(MutationObserver ? { MutationObserver } : {}),
    onChange: (page) => dispatch({ type: 'page', page }),
  });

  /** @returns {Media | null} */
  function readMedia() {
    if (bound === null) return null;
    const v = bound.video;
    return {
      position: v.currentTime,
      playbackRate: v.playbackRate,
      duration: v.duration,
      paused: v.paused,
      ended: v.ended,
      heldByUs: bound.gate.heldByUs,
      userPaused: bound.gate.userPaused,
      playRequested: bound.gate.playRequested,
    };
  }

  /** @param {HTMLVideoElement} video */
  function bind(video) {
    const onMedia = () => sync();
    const onPlay = () => {
      attachAudio(true);
      sync();
    };
    const gate = createGateController({ video, now, onPlayWhileHeld: onMedia, onUserPause: onMedia, onPlayError: onMedia });
    for (const type of GATE_EVENTS) video.addEventListener(type, onMedia);
    video.addEventListener('play', onPlay);
    bound = { video, gate, censor: null, graph: null, onMedia, onPlay, hits: null, method: null, offsetMs: null, mode: null };
    log?.('bound', video);
  }

  /** Let go of the current element, muting its gain (it no longer plays anything we know). */
  function unbind() {
    if (bound === null) return;
    const old = bound;
    bound = null;
    old.gate.stop();
    old.censor?.stop();
    old.graph?.muteNow();
    for (const type of GATE_EVENTS) old.video.removeEventListener(type, old.onMedia);
    old.video.removeEventListener('play', old.onPlay);
    dispatch({ type: 'audio-detached' });
  }

  /**
   * Route the element through Web Audio, once. `retry` also tries again after
   * a failure (a `play` press or a click), which `audio.attach` answers from
   * memory when the refusal is permanent.
   *
   * @param {boolean} retry
   */
  function attachAudio(retry) {
    const b = bound;
    if (b === null || b.graph !== null) return;
    if (!state.settings.enabled || state.page.phase !== 'watching') return;
    if (!retry && state.audio.status !== 'detached') return;
    const result = audio.attach(b.video);
    if (!result.ok) {
      dispatch({ type: 'audio', ok: false, error: result.error });
      return;
    }
    b.graph = result.graph;
    const d = deriveSession(state, readMedia());
    b.censor = createCensorController({
      video: b.video,
      graph: b.graph,
      hits: d.hits,
      method: d.method,
      offsetMs: d.offsetMs,
      mode: d.mode,
      setInterval,
      clearInterval,
    });
    Object.assign(b, { hits: d.hits, method: d.method, offsetMs: d.offsetMs, mode: d.mode });
    resumeAudio(b);
  }

  /** @param {Bound} b */
  function resumeAudio(b) {
    const graph = b.graph;
    if (graph === null) return;
    graph.resume().then((contextState) => {
      if (bound !== b) return;
      dispatch({ type: 'audio', ok: true, state: contextState });
      b.censor?.refresh();
    });
  }

  function clickUnfiltered() {
    dispatch({ type: 'unfiltered' });
    const b = bound;
    if (b === null) return;
    resumeAudio(b);
    playNow(b.video);
  }

  function clickEnable() {
    const b = bound;
    if (b === null) return;
    if (b.graph === null) attachAudio(true);
    else resumeAudio(b);
    playNow(b.video);
  }

  /** @param {HTMLVideoElement} video */
  function playNow(video) {
    try {
      const played = video.play();
      if (played && typeof played.then === 'function') played.then(undefined, () => {});
    } catch {
      // Refused (autoplay policy): the overlay stays and says why.
    }
  }

  function sync() {
    if (!started || stopped) return;
    if (syncing) {
      again = true;
      return;
    }
    syncing = true;
    try {
      do {
        again = false;
        syncOnce();
      } while (again && !stopped);
    } finally {
      syncing = false;
    }
  }

  function syncOnce() {
    const found = watcher.video;
    const video = found !== null && found.isConnected !== false ? found : null;
    if (video !== (bound?.video ?? null)) {
      unbind();
      if (video !== null) bind(video);
    }
    if (bound !== null && bound.graph === null && !bound.video.paused) attachAudio(false);

    const d = deriveSession(state, readMedia());
    derived = d;

    // Only what changed: sync runs on every timeupdate, and each setter re-plans.
    const b = bound;
    if (b !== null && b.censor !== null) {
      if (b.hits !== d.hits) {
        b.hits = d.hits;
        b.censor.setHits(d.hits);
      }
      if (b.method !== d.method) {
        b.method = d.method;
        b.censor.setMethod(d.method);
      }
      if (b.offsetMs !== d.offsetMs) {
        b.offsetMs = d.offsetMs;
        b.censor.setOffset(d.offsetMs);
      }
      if (b.mode !== d.mode) {
        b.mode = d.mode;
        b.censor.setMode(d.mode);
      }
    }
    bound?.gate.apply(d.gate);

    overlay.render(d.overlay, document.querySelector(PLAYER_SELECTOR));

    const badgeKey = JSON.stringify(d.badge);
    if (badgeKey !== lastBadge) {
      lastBadge = badgeKey;
      send(badgeMessage(d.badge)).catch?.(() => {});
    }

    scheduleHeartbeat();
  }

  function scheduleHeartbeat() {
    const at = nextHeartbeatAt(state);
    if (at === timerAt && (timer !== null || at === null)) return;
    if (timer !== null) clearTimeout(timer);
    timer = null;
    timerAt = at;
    if (at !== null) timer = setTimeout(beat, Math.max(0, at - now()));
  }

  function beat() {
    timer = null;
    timerAt = null;
    if (stopped) return;
    state = reduceSession(state, { type: 'tick', at: now() });
    if (heartbeatDue(state, now())) {
      const request = heartbeatRequest(state, readMedia());
      if (request !== null) {
        state = reduceSession(state, { type: 'heartbeat-sent', at: now(), position: request.position });
        const { generation, seq } = /** @type {import('./session.js').Inflight} */ (state.inflight);
        log?.('heartbeat', request);
        send(heartbeatMessage(request)).then((result) =>
          dispatch({ type: 'heartbeat-reply', generation, seq, result }),
        );
      }
    }
    sync();
  }

  const unsubscribe = settings.subscribe((next) => dispatch({ type: 'settings', settings: next }));
  settings.load().then((loaded) => dispatch({ type: 'settings', settings: loaded }));

  started = true;
  dispatch({ type: 'page', page: watcher.state });
  sync();

  return {
    /** The session state (for debugging and tests). */
    get state() {
      return state;
    },
    /** The outputs last applied. */
    get derived() {
      return derived;
    },
    /** The page watcher. */
    watcher,
    /** The overlay (for tests). */
    overlay,
    /** The censor controller of the current element, once the audio is attached. */
    get censor() {
      return bound?.censor ?? null;
    },
    /** The gate controller of the current element. */
    get gate() {
      return bound?.gate ?? null;
    },
    /** "Watch unfiltered", as the overlay's button does it. */
    clickUnfiltered,
    /** "Enable sound", as the overlay's button does it. */
    clickEnable,
    /** Stop everything. Idempotent; the gain is left muted. */
    stop() {
      if (stopped) return;
      stopped = true;
      unbind();
      watcher.stop();
      unsubscribe();
      if (timer !== null) clearTimeout(timer);
      timer = null;
      overlay.destroy();
    },
  };
}
