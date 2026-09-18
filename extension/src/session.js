// The content script's brain: everything the page knows about filtering the
// video it shows, as one immutable state and a reducer over events, plus
// pure functions that derive what to do from it. content-app.js only carries
// events in and applies the derived outputs; every decision is here.
//
// State (per page, frozen):
// - `page`, `generation`, `videoId`: the page watcher's latest state (W12).
//   Everything about a video is keyed on the generation.
// - `view`: the last good WatchView for this generation (never an error).
//   `hits`: the Hits in force, kept across `unchanged` replies. `session` and
//   `revision`: echoed back as `session`/`since` for cheap polling (W10).
// - `lastError`, `consecutiveErrors`: the heartbeat's failures since the
//   last good reply.
// - `unfiltered`: the user chose "Watch unfiltered" (for this generation).
// - `settings`, `settingsLoaded`.
// - `audio`: the Web Audio graph of the current <video> element:
//   `detached` (not attached yet), `running`, `suspended` (autoplay policy:
//   silent until a click), or `failed` with the W14 `{kind: 'audio'}` error.
//   It belongs to the element, not the video, so a navigation keeps it.
// - `inflight`: the heartbeat awaiting its reply, `{generation, seq, at}`;
//   `nextSeq`; `lastSentAt` (this generation); `lastPosition` (the playhead
//   last reported, reused during an ad).
//
// Events:
//   {type: 'page', page}                         the page watcher's state changed
//   {type: 'settings', settings}                 loaded or changed
//   {type: 'unfiltered'}                         "Watch unfiltered" clicked
//   {type: 'audio', ok: true, state}             attached; the context's state after resume()
//   {type: 'audio', ok: false, error}            attach refused
//   {type: 'audio-detached'}                     the <video> element changed
//   {type: 'heartbeat-sent', at, position}       a heartbeat went out (ms clock)
//   {type: 'heartbeat-reply', generation, seq, result}   its reply (a protocol Reply)
//   {type: 'tick', at}                           time passes: expires a lost heartbeat
//
// Decided (W15):
// - A new generation, or leaving `watching`, forgets the video: view, hits,
//   session/revision, errors, `unfiltered`, the heartbeat in flight and its
//   cadence. A reply is used only if its generation *and* sequence number
//   match the heartbeat in flight, so a late answer about the previous video
//   can never reach this one.
// - `unchanged` keeps the Hits we hold. An `unchanged` reply we cannot use
//   (a different session, or no Hits held) is dropped and `since` forgotten,
//   so the next heartbeat asks for everything. A new session replaces the
//   Hits outright. A view for another video is a `bad_response`.
// - Heartbeat cadence: every HEARTBEAT_MS (1 s) while working; every
//   SETTLED_HEARTBEAT_MS (5 s) once the view is `complete`, `failed` or
//   `unsupported`. The server keeps a working session only while POSTs come
//   (idle timeout 2 min) and a complete one 30 min after the last; 5 s keeps
//   it alive, picks up an edited Bad Words List, and retries a failed
//   session soon after the server's 1 min retention. It carries on during a
//   hold and while paused (transcription keeps going ahead). None when
//   disabled, unfiltered, not watching (idle, leaving), before the settings
//   are loaded, or when the element's audio failed (nothing can be censored
//   until a reload; a later successful attach resumes them). One heartbeat
//   at a time; one lost for INFLIGHT_TIMEOUT_MS counts as a `timeout` error.
// - During an ad the playhead reported is the last one reported before it:
//   `currentTime` then belongs to the ad (W16 to verify).
// - Error tolerance: a failed heartbeat is shown to the gate (which holds,
//   fail-closed) only when there is no view to go on, after ERROR_TOLERANCE
//   (3) in a row, or at once when the covered stretch ahead is already under
//   the gate's hold threshold (it would hold anyway; this gives it the true
//   reason). Until then the last view stands: its coverage is final, so
//   playing the covered stretch is safe. The gate itself ignores server
//   errors once the view is `complete`. An audio error always goes through.
// - `audio: suspended` holds the video (a context that is not running means
//   silence) with "Click to enable FoulFilter" instead of the gate's reason.
// - Censor mode: `open` when disabled, during an ad, or unfiltered; `mute`
//   whenever the page is not `watching` (leaving, and idle: the miniplayer),
//   while the gate holds (the video should be paused; a play press cannot
//   leak), and with no element; else `filter`.
// - Miniplayer (W12's open problem): on a non-watch page YouTube's miniplayer
//   keeps playing the previous video in the same element while the phase is
//   `idle`. V1 mutes it (fail-closed) and says so in the overlay and the
//   badge. Filtering it with the last Hits was rejected: nothing proves the
//   element still plays that video (it may have moved on to the next in a
//   queue), and a wrong guess is audible. The cost: the miniplayer is silent.
// - `enabled: false`: censor mode `open`, no heartbeats, no overlay, no hold
//   (the gate sees it as unfiltered), badge OFF.

import { HIDDEN_OVERLAY, HOLD_SECONDS, decideGate, effectiveRate, gateOverlay, mediaAhead } from './gate.js';
import { INITIAL_PAGE_STATE, isFilterable } from './page-state.js';
import { DEFAULT_SETTINGS, normaliseSettings } from './settings.js';

/**
 * @typedef {import('./page-state.js').PageState} PageState
 * @typedef {import('./api-client.js').WatchView} WatchView
 * @typedef {import('./api-client.js').Hit} Hit
 * @typedef {import('./settings.js').Settings} Settings
 * @typedef {import('./gate.js').GateInput} GateInput
 * @typedef {import('./gate.js').GateDecision} GateDecision
 * @typedef {import('./gate.js').GateError} GateError
 * @typedef {import('./gate.js').OverlayModel} OverlayModel
 * @typedef {import('./censor-controller.js').CensorMode} CensorMode
 *
 * @typedef {'detached' | 'running' | 'suspended' | 'failed'} AudioStatus
 * @typedef {{ status: AudioStatus, error: GateError | null }} AudioState
 * @typedef {{ generation: number, seq: number, at: number }} Inflight
 *
 * @typedef {object} SessionState
 * @property {PageState} page
 * @property {number} generation
 * @property {string | null} videoId
 * @property {WatchView | null} view
 * @property {ReadonlyArray<Hit> | null} hits
 * @property {string | null} session
 * @property {number | null} revision
 * @property {GateError | null} lastError
 * @property {number} consecutiveErrors
 * @property {boolean} unfiltered
 * @property {Settings} settings
 * @property {boolean} settingsLoaded
 * @property {AudioState} audio
 * @property {Inflight | null} inflight
 * @property {number} nextSeq
 * @property {number | null} lastSentAt
 * @property {number} lastPosition
 *
 * @typedef {object} Media  What the element and the gate controller say now.
 * @property {number} position  `video.currentTime`.
 * @property {number} playbackRate
 * @property {number | null} duration  `video.duration`.
 * @property {boolean} paused
 * @property {boolean} ended
 * @property {boolean} heldByUs
 * @property {boolean} userPaused
 * @property {boolean} playRequested
 *
 * @typedef {object} Badge
 * @property {string} text
 * @property {string} color  `#rrggbb`.
 * @property {string} title
 *
 * @typedef {object} Derived
 * @property {GateInput} input  The W13 gate input.
 * @property {GateDecision | SessionHold} gate  What the gate controller applies.
 * @property {CensorMode} mode
 * @property {ReadonlyArray<Hit>} hits
 * @property {'silence' | 'bleep'} method
 * @property {number} offsetMs
 * @property {OverlayModel & { showEnable?: boolean }} overlay
 * @property {Badge} badge
 * @property {boolean} miniplayer  The miniplayer is playing and muted.
 *
 * @typedef {{ action: 'hold', reason: 'audio-suspended', heldByUs: true, resume: false }} SessionHold
 */

/** Heartbeat period while the session is working, ms. */
export const HEARTBEAT_MS = 1000;

/** Heartbeat period once the view is complete, failed or unsupported, ms. */
export const SETTLED_HEARTBEAT_MS = 5000;

/** A heartbeat with no reply after this long (ms) is given up as a timeout. */
export const INFLIGHT_TIMEOUT_MS = 10000;

/** Failed heartbeats in a row before the gate is told (unless it matters sooner). */
export const ERROR_TOLERANCE = 3;

/** View states after which nothing more is coming quickly. */
const SETTLED_STATES = Object.freeze(['complete', 'failed', 'unsupported']);

/** Badge colours. */
export const BADGE_COLORS = Object.freeze({
  idle: '#5f6368',
  preparing: '#1a73e8',
  filtering: '#188038',
  problem: '#d93025',
  muted: '#e37400',
  off: '#5f6368',
});

/** @type {ReadonlyArray<Hit>} */
const NO_HITS = Object.freeze([]);

/** @type {Readonly<AudioState>} */
const AUDIO_DETACHED = Object.freeze({ status: 'detached', error: null });

/** Media when there is no element. @type {Readonly<Media>} */
export const NO_MEDIA = Object.freeze({
  position: 0,
  playbackRate: 1,
  duration: null,
  paused: true,
  ended: false,
  heldByUs: false,
  userPaused: false,
  playRequested: false,
});

/** Everything about the current video, as it is before the first answer. */
const FRESH_VIDEO = Object.freeze({
  view: null,
  hits: null,
  session: null,
  revision: null,
  lastError: null,
  consecutiveErrors: 0,
  unfiltered: false,
  inflight: null,
  lastSentAt: null,
  lastPosition: 0,
});

/** @type {Readonly<SessionState>} */
export const INITIAL_SESSION = Object.freeze({
  page: INITIAL_PAGE_STATE,
  generation: 0,
  videoId: null,
  ...FRESH_VIDEO,
  settings: DEFAULT_SETTINGS,
  settingsLoaded: false,
  audio: AUDIO_DETACHED,
  nextSeq: 1,
});

/**
 * `state` with `changes`, or `state` itself when they change nothing.
 *
 * @param {SessionState} state
 * @param {Partial<SessionState>} changes
 * @returns {SessionState}
 */
function with_(state, changes) {
  const keys = /** @type {(keyof SessionState)[]} */ (Object.keys(changes));
  if (keys.every((key) => Object.is(state[key], changes[key]))) return state;
  return Object.freeze({ ...state, ...changes });
}

/**
 * @param {unknown} value
 * @returns {value is Record<string, any>}
 */
function isObject(value) {
  return typeof value === 'object' && value !== null;
}

/** @param {unknown} value @returns {value is number} */
function isFiniteNumber(value) {
  return typeof value === 'number' && Number.isFinite(value);
}

/**
 * The state after one event. Never mutates; returns `state` itself when
 * nothing changed. Malformed events change nothing.
 *
 * @param {SessionState} state
 * @param {unknown} event
 * @returns {SessionState}
 */
export function reduceSession(state, event) {
  if (!isObject(event)) return state;
  switch (event.type) {
    case 'page':
      return onPage(state, event.page);
    case 'settings':
      return isObject(event.settings)
        ? with_(state, { settings: normaliseSettings(event.settings), settingsLoaded: true })
        : state;
    case 'unfiltered':
      return state.page.phase === 'watching' ? with_(state, { unfiltered: true }) : state;
    case 'audio':
      return onAudio(state, event);
    case 'audio-detached':
      return with_(state, { audio: AUDIO_DETACHED });
    case 'heartbeat-sent':
      return onSent(state, event);
    case 'heartbeat-reply':
      return onReply(state, event);
    case 'tick':
      return onTick(state, event.at);
    default:
      return state;
  }
}

/**
 * @param {SessionState} state
 * @param {unknown} page
 */
function onPage(state, page) {
  if (!isObject(page) || typeof page.phase !== 'string' || !isFiniteNumber(page.generation)) return state;
  const p = /** @type {PageState} */ (page);
  const videoId = p.phase === 'watching' ? p.videoId : null;
  const same = p.phase === 'watching' && p.generation === state.generation && videoId === state.videoId;
  if (same) return with_(state, { page: p });
  return with_(state, { page: p, generation: p.generation, videoId, ...FRESH_VIDEO });
}

/**
 * @param {SessionState} state
 * @param {Record<string, any>} event
 */
function onAudio(state, event) {
  if (event.ok === true) {
    const status = event.state === 'running' ? 'running' : 'suspended';
    return state.audio.status === status ? state : with_(state, { audio: Object.freeze({ status, error: null }) });
  }
  if (event.ok === false && isObject(event.error)) {
    return with_(state, { audio: Object.freeze({ status: 'failed', error: /** @type {GateError} */ (event.error) }) });
  }
  return state;
}

/**
 * @param {SessionState} state
 * @param {Record<string, any>} event
 */
function onSent(state, event) {
  if (!isFiniteNumber(event.at) || state.page.phase !== 'watching') return state;
  const position = isFiniteNumber(event.position) && event.position >= 0 ? event.position : state.lastPosition;
  return with_(state, {
    inflight: Object.freeze({ generation: state.generation, seq: state.nextSeq, at: event.at }),
    nextSeq: state.nextSeq + 1,
    lastSentAt: event.at,
    lastPosition: position,
  });
}

/**
 * @param {SessionState} state
 * @param {Record<string, any>} event
 */
function onReply(state, event) {
  const inflight = state.inflight;
  if (inflight === null || event.seq !== inflight.seq || event.generation !== state.generation) return state;
  const result = event.result;
  const answered = with_(state, { inflight: null });

  if (!isObject(result) || result.ok !== true) {
    const error = isObject(result) && isObject(result.error) && typeof result.error.kind === 'string'
      ? /** @type {GateError} */ (result.error)
      : { kind: 'extension', detail: 'The extension gave no usable answer.' };
    return failed(answered, error);
  }

  const view = /** @type {WatchView} */ (result.view);
  if (!isObject(view) || view.videoId !== state.videoId) {
    return failed(answered, { kind: 'bad_response', detail: 'The answer was about another video.' });
  }

  const good = { lastError: null, consecutiveErrors: 0 };
  if (view.unchanged) {
    if (view.session !== state.session || state.hits === null) {
      // Nothing to keep: ask for everything next time.
      return with_(answered, { ...good, session: null, revision: null });
    }
    return with_(answered, { ...good, view, revision: view.revision });
  }
  return with_(answered, {
    ...good,
    view,
    hits: Object.freeze([...(view.hits ?? [])]),
    session: view.session,
    revision: view.revision,
  });
}

/**
 * @param {SessionState} state
 * @param {GateError} error
 */
function failed(state, error) {
  return with_(state, { lastError: error, consecutiveErrors: state.consecutiveErrors + 1 });
}

/**
 * @param {SessionState} state
 * @param {unknown} at
 */
function onTick(state, at) {
  const inflight = state.inflight;
  if (!isFiniteNumber(at) || inflight === null || at - inflight.at < INFLIGHT_TIMEOUT_MS) return state;
  return failed(with_(state, { inflight: null }), {
    kind: 'timeout',
    detail: `No answer from the extension within ${INFLIGHT_TIMEOUT_MS / 1000} s.`,
  });
}

// ---------------------------------------------------------------- heartbeat

/**
 * Whether heartbeats should be flowing at all.
 *
 * @param {SessionState} state
 */
export function shouldHeartbeat(state) {
  return (
    state.settingsLoaded &&
    state.settings.enabled &&
    state.page.phase === 'watching' &&
    state.videoId !== null &&
    !state.unfiltered &&
    state.audio.status !== 'failed'
  );
}

/**
 * The period between heartbeats now, ms.
 *
 * @param {SessionState} state
 */
export function heartbeatCadence(state) {
  return state.view !== null && SETTLED_STATES.includes(state.view.state) ? SETTLED_HEARTBEAT_MS : HEARTBEAT_MS;
}

/**
 * When (ms clock) something heartbeat-related is next due, or null for never:
 * the next heartbeat, or the time a heartbeat in flight is given up.
 *
 * @param {SessionState} state
 * @returns {number | null}
 */
export function nextHeartbeatAt(state) {
  if (!shouldHeartbeat(state)) return null;
  if (state.inflight !== null) return state.inflight.at + INFLIGHT_TIMEOUT_MS;
  if (state.lastSentAt === null) return 0;
  return state.lastSentAt + heartbeatCadence(state);
}

/**
 * Whether a heartbeat should be sent at `now`.
 *
 * @param {SessionState} state
 * @param {number} now
 */
export function heartbeatDue(state, now) {
  if (!shouldHeartbeat(state) || state.inflight !== null) return false;
  return state.lastSentAt === null || now >= state.lastSentAt + heartbeatCadence(state);
}

/**
 * The heartbeat to send (the fields of protocol.js's `heartbeatMessage`), or
 * null when none should go.
 *
 * @param {SessionState} state
 * @param {Media | null} media
 * @returns {{ provider: 'youtube', videoId: string, position: number, since: number | null, session: string | null } | null}
 */
export function heartbeatRequest(state, media) {
  if (!shouldHeartbeat(state) || state.videoId === null) return null;
  const read = media?.position;
  const position = !state.page.adShowing && isFiniteNumber(read) && read >= 0 ? read : state.lastPosition;
  const since = state.session !== null && state.revision !== null;
  return {
    provider: 'youtube',
    videoId: state.videoId,
    position,
    since: since ? state.revision : null,
    session: since ? state.session : null,
  };
}

// ---------------------------------------------------------------- the gate

/**
 * The error the gate should see now, if any (see "Error tolerance").
 *
 * @param {SessionState} state
 * @param {Media} media
 * @returns {GateError | null}
 */
export function gateError(state, media) {
  if (state.audio.status === 'failed') return state.audio.error;
  if (state.lastError === null || state.consecutiveErrors < 1) return null;
  if (state.view === null || state.consecutiveErrors >= ERROR_TOLERANCE) return state.lastError;
  const ahead = mediaAhead(state.view, { ...media, page: state.page, view: state.view });
  return ahead < HOLD_SECONDS * effectiveRate(media.playbackRate) ? state.lastError : null;
}

/**
 * The W13 gate input, exactly.
 *
 * @param {SessionState} state
 * @param {Media | null} [media]
 * @returns {GateInput}
 */
export function gateInput(state, media) {
  const m = media ?? NO_MEDIA;
  return {
    page: state.page,
    view: state.view,
    error: gateError(state, m),
    unfiltered: state.unfiltered || !state.settings.enabled,
    failPolicy: state.settings.failPolicy,
    position: m.position,
    playbackRate: m.playbackRate,
    duration: m.duration,
    paused: m.paused,
    ended: m.ended,
    heldByUs: m.heldByUs,
    userPaused: m.userPaused,
    playRequested: m.playRequested,
    serverUrl: state.settings.serverUrl,
  };
}

/** @type {SessionHold} */
const SUSPENDED_HOLD = Object.freeze({ action: 'hold', reason: 'audio-suspended', heldByUs: true, resume: false });

/**
 * The miniplayer case: a non-watch page whose element, routed through our
 * graph, is playing.
 *
 * @param {SessionState} state
 * @param {Media} media
 */
export function isMiniplayerMuted(state, media) {
  return (
    state.settings.enabled &&
    state.page.phase === 'idle' &&
    state.page.hasVideoElement &&
    (state.audio.status === 'running' || state.audio.status === 'suspended') &&
    !media.paused &&
    !media.ended
  );
}

/**
 * @param {SessionState} state
 * @param {GateDecision | SessionHold} gate
 * @returns {CensorMode}
 */
function censorMode(state, gate) {
  const page = state.page;
  if (!state.settings.enabled) return 'open';
  if (page.phase !== 'watching') return 'mute';
  if (page.adShowing) return 'open';
  if (state.unfiltered) return 'open';
  if (gate.action === 'hold') return 'mute';
  if (!page.hasVideoElement) return 'mute';
  return 'filter';
}

/** @type {Readonly<OverlayModel & { showEnable: boolean }>} */
const MINIPLAYER_OVERLAY = Object.freeze({
  visible: true,
  title: 'FoulFilter muted the miniplayer',
  detail: 'It cannot tell what the miniplayer is playing. Open the video to watch it filtered.',
  percent: null,
  showUnfiltered: false,
  showEnable: false,
});

/** @type {Readonly<OverlayModel & { showEnable: boolean }>} */
const SUSPENDED_OVERLAY = Object.freeze({
  visible: true,
  title: 'Click to enable FoulFilter',
  detail: 'Chrome needs a click on the page before FoulFilter can play this video’s sound.',
  percent: null,
  showUnfiltered: true,
  showEnable: true,
});

/** Gate reasons that are a problem rather than a wait. */
const PROBLEM_REASONS = Object.freeze(['error', 'failed', 'unsupported', 'not-keeping-up', 'audio-suspended']);

/**
 * @param {string} text
 * @param {string} color
 * @param {string} title
 * @returns {Badge}
 */
function badge(text, color, title) {
  return Object.freeze({ text, color, title });
}

/**
 * @param {SessionState} state
 * @param {GateDecision | SessionHold} gate
 * @param {OverlayModel} overlay
 * @param {boolean} miniplayer
 * @returns {Badge}
 */
function badgeFor(state, gate, overlay, miniplayer) {
  const page = state.page;
  if (!state.settings.enabled) return badge('OFF', BADGE_COLORS.off, 'FoulFilter is off. Turn it on in its options.');
  if (miniplayer) return badge('MUTE', BADGE_COLORS.muted, `${MINIPLAYER_OVERLAY.title}. ${MINIPLAYER_OVERLAY.detail}`);
  if (page.phase !== 'watching') return badge('', BADGE_COLORS.idle, 'FoulFilter');
  if (state.unfiltered) return badge('OFF', BADGE_COLORS.off, 'FoulFilter: watching this video unfiltered.');
  if (page.adShowing) return badge('AD', BADGE_COLORS.idle, 'FoulFilter stands aside while an ad plays.');
  if (gate.action === 'hold') {
    const title = `FoulFilter: ${overlay.title}${typeof overlay.percent === 'number' ? ` (${overlay.percent}%)` : ''}.`;
    return PROBLEM_REASONS.includes(gate.reason)
      ? badge('!', BADGE_COLORS.problem, title)
      : badge('…', BADGE_COLORS.preparing, title);
  }
  if (!page.hasVideoElement) return badge('…', BADGE_COLORS.preparing, 'FoulFilter is looking for the video.');
  const view = state.view;
  const progress = view !== null && view.state !== 'complete' ? ` (${Math.floor(view.progress * 100)}% prepared)` : '';
  return badge('✓', BADGE_COLORS.filtering, `FoulFilter is filtering this video${progress}.`);
}

/**
 * Everything the content script applies, from the state and the element.
 *
 * @param {SessionState} state
 * @param {Media | null} [media]  Null when there is no element.
 * @returns {Derived}
 */
export function deriveSession(state, media) {
  const m = media ?? NO_MEDIA;
  const input = gateInput(state, m);
  /** @type {GateDecision | SessionHold} */
  let gate = decideGate(input);
  const suspended =
    state.audio.status === 'suspended' && state.settings.enabled && isFilterable(state.page) && !m.ended;
  if (suspended) gate = SUSPENDED_HOLD;

  const miniplayer = isMiniplayerMuted(state, m);
  /** @type {OverlayModel & { showEnable?: boolean }} */
  let overlay;
  if (!state.settings.enabled) overlay = HIDDEN_OVERLAY;
  else if (miniplayer) overlay = MINIPLAYER_OVERLAY;
  else if (suspended) overlay = SUSPENDED_OVERLAY;
  else overlay = gateOverlay(input, /** @type {GateDecision} */ (gate));

  return {
    input,
    gate,
    mode: censorMode(state, gate),
    hits: state.hits ?? NO_HITS,
    method: state.settings.censorMethod === 'bleep' ? 'bleep' : 'silence',
    offsetMs: state.settings.offsetMs,
    overlay,
    badge: badgeFor(state, gate, overlay, miniplayer),
    miniplayer,
  };
}
