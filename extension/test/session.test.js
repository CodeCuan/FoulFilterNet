// session.js: the content script's reducer and everything derived from it
// (heartbeat cadence, the W13 gate input, censor mode, overlay, badge).
// End-to-end decisions go through the real decideGate.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  BADGE_COLORS,
  ERROR_TOLERANCE,
  HEARTBEAT_MS,
  INFLIGHT_TIMEOUT_MS,
  INITIAL_SESSION,
  NO_MEDIA,
  SETTLED_HEARTBEAT_MS,
  FAST_HEARTBEAT_MS,
  deriveSession,
  gateError,
  gateInput,
  heartbeatCadence,
  heartbeatDue,
  heartbeatRequest,
  isMiniplayerMuted,
  isStartingUp,
  nextHeartbeatAt,
  reduceSession,
  shouldHeartbeat,
} from '../src/session.js';
import { HIDDEN_OVERLAY, RESUME_SECONDS, decideGate, gateOverlay } from '../src/gate.js';
import { INITIAL_PAGE_STATE } from '../src/page-state.js';
import { DEFAULT_SETTINGS } from '../src/settings.js';

const A = 'YwARwww5aFo';
const B = 'jNQXAC9IVRw';

/** A page state; watching A in generation 1 unless overridden. */
const page = (over = {}) =>
  Object.freeze({ phase: 'watching', videoId: A, adShowing: false, hasVideoElement: true, generation: 1, ...over });

const HIT = Object.freeze({ start: 10, end: 11, phrase: 'x' });

/** A WatchView as api-client.js parses it. */
const view = (over = {}) => ({
  key: `youtube-${over.videoId ?? A}`,
  provider: 'youtube',
  videoId: A,
  session: 's1',
  state: 'transcribing',
  reason: null,
  failureKind: null,
  title: null,
  duration: 649,
  revision: 1,
  unchanged: false,
  coverage: [[0, 46]],
  hits: [HIT],
  windowsDone: 2,
  windowsTotal: 30,
  progress: 0.07,
  realtimeFactor: 25,
  keepingUp: true,
  fromCache: false,
  ...over,
});

const unchanged = (over = {}) => view({ unchanged: true, hits: null, ...over });

/** Element and gate-controller facts; a video playing at 0 unless overridden. */
const media = (over = {}) => ({
  position: 0,
  playbackRate: 1,
  duration: 649,
  paused: false,
  ended: false,
  heldByUs: false,
  userPaused: false,
  playRequested: false,
  ...over,
});

const loaded = (settings = {}) => ({ type: 'settings', settings: { ...DEFAULT_SETTINGS, ...settings } });
const onPage = (over = {}) => ({ type: 'page', page: page(over) });

/** @param {...unknown} events */
const run = (...events) => events.reduce(reduceSession, INITIAL_SESSION);

/** Settings loaded, watching A in generation 1. */
const watching = (over = {}) => run(loaded(), onPage(over));

const sent = (state, at = 1000, position = 0) => reduceSession(state, { type: 'heartbeat-sent', at, position });

const reply = (state, result) =>
  reduceSession(state, { type: 'heartbeat-reply', generation: state.inflight.generation, seq: state.inflight.seq, result });

/** A heartbeat sent at `at` and answered with `v`. */
const answered = (state, v = view(), at = 1000) => reply(sent(state, at), { ok: true, view: v });

/** A heartbeat sent at `at` and failed with `kind`. */
const errored = (state, kind = 'unreachable', at = 1000) => reply(sent(state, at), { ok: false, error: { kind } });

const failTimes = (state, n) => {
  let s = state;
  for (let i = 0; i < n; i++) s = errored(s, 'unreachable', 1000 + i * 1000);
  return s;
};

const audioRunning = { type: 'audio', ok: true, state: 'running' };
const audioSuspended = { type: 'audio', ok: true, state: 'suspended' };
const AUDIO_ERROR = Object.freeze({ kind: 'audio', reason: 'already-connected', detail: 'Reload the page to filter it.' });
const audioFailed = { type: 'audio', ok: false, error: AUDIO_ERROR };

describe('session: constants', () => {
  it('heartbeats every second while working', () => {
    assert.equal(HEARTBEAT_MS, 1000);
  });

  it('heartbeats every five seconds once settled', () => {
    assert.equal(SETTLED_HEARTBEAT_MS, 5000);
  });

  it('keeps the settled cadence well inside the server idle timeout (2 min)', () => {
    assert.ok(SETTLED_HEARTBEAT_MS * 10 < 120_000);
  });

  it('gives up a heartbeat after ten seconds', () => {
    assert.equal(INFLIGHT_TIMEOUT_MS, 10_000);
  });

  it('gives up later than the api-client timeout (3 s)', () => {
    assert.ok(INFLIGHT_TIMEOUT_MS > 3000);
  });

  it('tolerates three errors in a row', () => {
    assert.equal(ERROR_TOLERANCE, 3);
  });

  it('uses #rrggbb badge colours', () => {
    for (const color of Object.values(BADGE_COLORS)) assert.match(color, /^#[0-9a-f]{6}$/);
  });
});

describe('session: initial state', () => {
  it('is frozen', () => {
    assert.ok(Object.isFrozen(INITIAL_SESSION));
  });

  it('starts at generation 0', () => {
    assert.equal(INITIAL_SESSION.generation, 0);
  });

  it('has the initial page state', () => {
    assert.equal(INITIAL_SESSION.page, INITIAL_PAGE_STATE);
  });

  it('has no view', () => {
    assert.equal(INITIAL_SESSION.view, null);
  });

  it('has no hits', () => {
    assert.equal(INITIAL_SESSION.hits, null);
  });

  it('has no session or revision', () => {
    assert.deepEqual([INITIAL_SESSION.session, INITIAL_SESSION.revision], [null, null]);
  });

  it('has the default settings, not yet loaded', () => {
    assert.deepEqual([INITIAL_SESSION.settings, INITIAL_SESSION.settingsLoaded], [DEFAULT_SETTINGS, false]);
  });

  it('has the audio detached', () => {
    assert.equal(INITIAL_SESSION.audio.status, 'detached');
  });

  it('has nothing in flight', () => {
    assert.equal(INITIAL_SESSION.inflight, null);
  });

  it('numbers heartbeats from 1', () => {
    assert.equal(INITIAL_SESSION.nextSeq, 1);
  });
});

describe('session: the reducer in general', () => {
  it('returns the same state for an unknown event', () => {
    const s = watching();
    assert.equal(reduceSession(s, { type: 'nonsense' }), s);
  });

  it('returns the same state for a non-object event', () => {
    const s = watching();
    assert.equal(reduceSession(s, 'page'), s);
  });

  it('returns the same state for null', () => {
    const s = watching();
    assert.equal(reduceSession(s, null), s);
  });

  it('returns the same state when an event changes nothing', () => {
    const s = watching();
    assert.equal(reduceSession(s, { type: 'page', page: s.page }), s);
  });

  it('returns frozen states', () => {
    assert.ok(Object.isFrozen(answered(watching())));
  });

  it('never mutates the state it is given', () => {
    const s = watching();
    const copy = JSON.stringify(s);
    answered(s);
    assert.equal(JSON.stringify(s), copy);
  });
});

describe('session: page events', () => {
  it('takes the generation from the page', () => {
    assert.equal(watching({ generation: 4 }).generation, 4);
  });

  it('takes the video id while watching', () => {
    assert.equal(watching().videoId, A);
  });

  it('keeps the page state', () => {
    assert.deepEqual(watching().page, page());
  });

  it('has no video id while idle', () => {
    assert.equal(run(loaded(), onPage({ phase: 'idle', videoId: null })).videoId, null);
  });

  it('ignores a page without a phase', () => {
    const s = watching();
    assert.equal(reduceSession(s, { type: 'page', page: { generation: 2 } }), s);
  });

  it('ignores a page without a generation', () => {
    const s = watching();
    assert.equal(reduceSession(s, { type: 'page', page: { phase: 'idle' } }), s);
  });

  it('ignores a missing page', () => {
    const s = watching();
    assert.equal(reduceSession(s, { type: 'page' }), s);
  });

  it('keeps the view when an ad starts in the same generation', () => {
    const s = reduceSession(answered(watching()), onPage({ adShowing: true }));
    assert.notEqual(s.view, null);
  });

  it('keeps the hits when the element is replaced in the same generation', () => {
    const s = reduceSession(answered(watching()), onPage({ hasVideoElement: false }));
    assert.deepEqual(s.hits, [HIT]);
  });

  it('drops the view on a new generation', () => {
    assert.equal(reduceSession(answered(watching()), onPage({ generation: 2, videoId: B })).view, null);
  });

  it('drops the hits on a new generation', () => {
    assert.equal(reduceSession(answered(watching()), onPage({ generation: 2, videoId: B })).hits, null);
  });

  it('drops the session and revision on a new generation', () => {
    const s = reduceSession(answered(watching()), onPage({ generation: 2, videoId: B }));
    assert.deepEqual([s.session, s.revision], [null, null]);
  });

  it('drops the view on a new generation of the same video', () => {
    assert.equal(reduceSession(answered(watching()), onPage({ generation: 2 })).view, null);
  });

  it('drops the errors on a new generation', () => {
    const s = reduceSession(failTimes(watching(), 3), onPage({ generation: 2 }));
    assert.deepEqual([s.lastError, s.consecutiveErrors], [null, 0]);
  });

  it('drops unfiltered on a new generation', () => {
    const s = reduceSession(reduceSession(watching(), { type: 'unfiltered' }), onPage({ generation: 2, videoId: B }));
    assert.equal(s.unfiltered, false);
  });

  it('drops the heartbeat in flight on a new generation', () => {
    assert.equal(reduceSession(sent(watching()), onPage({ generation: 2, videoId: B })).inflight, null);
  });

  it('restarts the cadence on a new generation', () => {
    assert.equal(reduceSession(sent(watching()), onPage({ generation: 2, videoId: B })).lastSentAt, null);
  });

  it('keeps the sequence counter across generations', () => {
    assert.equal(reduceSession(sent(watching()), onPage({ generation: 2, videoId: B })).nextSeq, 2);
  });

  it('drops the view when leaving', () => {
    assert.equal(reduceSession(answered(watching()), onPage({ phase: 'leaving', videoId: null })).view, null);
  });

  it('drops the hits when leaving', () => {
    assert.equal(reduceSession(answered(watching()), onPage({ phase: 'leaving', videoId: null })).hits, null);
  });

  it('drops the view when going idle', () => {
    assert.equal(reduceSession(answered(watching()), onPage({ phase: 'idle', videoId: null })).view, null);
  });

  it('drops unfiltered when leaving', () => {
    const s = reduceSession(watching(), { type: 'unfiltered' });
    assert.equal(reduceSession(s, onPage({ phase: 'leaving', videoId: null })).unfiltered, false);
  });

  it('keeps the audio state across generations (it belongs to the element)', () => {
    const s = reduceSession(reduceSession(watching(), audioRunning), onPage({ generation: 2, videoId: B }));
    assert.equal(s.audio.status, 'running');
  });

  it('keeps an audio failure across generations', () => {
    const s = reduceSession(reduceSession(watching(), audioFailed), onPage({ generation: 2, videoId: B }));
    assert.equal(s.audio.status, 'failed');
  });

  it('keeps the settings across generations', () => {
    const s = reduceSession(run(loaded({ censorMethod: 'bleep' }), onPage()), onPage({ generation: 2 }));
    assert.equal(s.settings.censorMethod, 'bleep');
  });
});

describe('session: settings', () => {
  it('marks the settings loaded', () => {
    assert.equal(run(loaded()).settingsLoaded, true);
  });

  it('takes the settings', () => {
    assert.equal(run(loaded({ enabled: false })).settings.enabled, false);
  });

  it('normalises them', () => {
    assert.equal(run({ type: 'settings', settings: { censorMethod: 'remove' } }).settings.censorMethod, 'silence');
  });

  it('falls back per field for bad values', () => {
    assert.equal(run({ type: 'settings', settings: { offsetMs: 'soon' } }).settings.offsetMs, 0);
  });

  it('ignores a missing settings object', () => {
    assert.equal(run({ type: 'settings' }), INITIAL_SESSION);
  });

  it('keeps the view when the settings change', () => {
    assert.notEqual(reduceSession(answered(watching()), loaded({ offsetMs: 40 })).view, null);
  });
});

describe('session: Watch unfiltered', () => {
  it('sets unfiltered while watching', () => {
    assert.equal(reduceSession(watching(), { type: 'unfiltered' }).unfiltered, true);
  });

  it('is ignored while idle', () => {
    const s = run(loaded(), onPage({ phase: 'idle', videoId: null }));
    assert.equal(reduceSession(s, { type: 'unfiltered' }), s);
  });

  it('is ignored while leaving', () => {
    const s = run(loaded(), onPage({ phase: 'leaving', videoId: null }));
    assert.equal(reduceSession(s, { type: 'unfiltered' }).unfiltered, false);
  });

  it('keeps the view', () => {
    assert.notEqual(reduceSession(answered(watching()), { type: 'unfiltered' }).view, null);
  });
});

describe('session: audio', () => {
  it('records a running context', () => {
    assert.equal(reduceSession(watching(), audioRunning).audio.status, 'running');
  });

  it('records a suspended context', () => {
    assert.equal(reduceSession(watching(), audioSuspended).audio.status, 'suspended');
  });

  it('counts any state other than running as suspended', () => {
    assert.equal(reduceSession(watching(), { type: 'audio', ok: true, state: 'interrupted' }).audio.status, 'suspended');
  });

  it('records a failure with its error', () => {
    assert.deepEqual(reduceSession(watching(), audioFailed).audio, { status: 'failed', error: AUDIO_ERROR });
  });

  it('goes back to detached when the element changes', () => {
    const s = reduceSession(reduceSession(watching(), audioRunning), { type: 'audio-detached' });
    assert.equal(s.audio.status, 'detached');
  });

  it('clears a failure when the element changes', () => {
    const s = reduceSession(reduceSession(watching(), audioFailed), { type: 'audio-detached' });
    assert.equal(s.audio.error, null);
  });

  it('recovers from suspended to running', () => {
    const s = reduceSession(reduceSession(watching(), audioSuspended), audioRunning);
    assert.equal(s.audio.status, 'running');
  });

  it('returns the same state for the same status', () => {
    const s = reduceSession(watching(), audioRunning);
    assert.equal(reduceSession(s, audioRunning), s);
  });

  it('ignores a failure without an error', () => {
    const s = watching();
    assert.equal(reduceSession(s, { type: 'audio', ok: false }), s);
  });

  it('ignores an event without ok', () => {
    const s = watching();
    assert.equal(reduceSession(s, { type: 'audio', state: 'running' }), s);
  });
});

describe('session: a heartbeat sent', () => {
  it('is in flight, tagged with the generation', () => {
    assert.equal(sent(watching({ generation: 3 })).inflight.generation, 3);
  });

  it('gets the next sequence number', () => {
    assert.equal(sent(watching()).inflight.seq, 1);
  });

  it('records when it went', () => {
    assert.equal(sent(watching(), 4242).inflight.at, 4242);
  });

  it('advances the sequence counter', () => {
    assert.equal(sent(watching()).nextSeq, 2);
  });

  it('numbers the next one after a reply', () => {
    assert.equal(sent(answered(watching())).inflight.seq, 2);
  });

  it('sets the cadence clock', () => {
    assert.equal(sent(watching(), 4242).lastSentAt, 4242);
  });

  it('remembers the playhead reported', () => {
    assert.equal(sent(watching(), 1000, 12.5).lastPosition, 12.5);
  });

  it('keeps the last playhead for a negative one', () => {
    assert.equal(sent(sent(watching(), 1000, 12.5), 2000, -1).lastPosition, 12.5);
  });

  it('keeps the last playhead for NaN', () => {
    assert.equal(sent(sent(watching(), 1000, 12.5), 2000, NaN).lastPosition, 12.5);
  });

  it('is ignored without a time', () => {
    const s = watching();
    assert.equal(reduceSession(s, { type: 'heartbeat-sent', position: 1 }), s);
  });

  it('is ignored while not watching', () => {
    const s = run(loaded(), onPage({ phase: 'idle', videoId: null }));
    assert.equal(sent(s), s);
  });
});

describe('session: heartbeat replies', () => {
  it('stores the view', () => {
    assert.equal(answered(watching()).view.state, 'transcribing');
  });

  it('takes the hits', () => {
    assert.deepEqual(answered(watching()).hits, [HIT]);
  });

  it('freezes its copy of the hits', () => {
    assert.ok(Object.isFrozen(answered(watching()).hits));
  });

  it('takes the session and revision for since', () => {
    const s = answered(watching(), view({ session: 'abc', revision: 7 }));
    assert.deepEqual([s.session, s.revision], ['abc', 7]);
  });

  it('clears the heartbeat in flight', () => {
    assert.equal(answered(watching()).inflight, null);
  });

  it('keeps the cadence clock from the send', () => {
    assert.equal(answered(watching(), view(), 1234).lastSentAt, 1234);
  });

  it('takes an empty hit list', () => {
    assert.deepEqual(answered(watching(), view({ hits: [] })).hits, []);
  });

  it('ignores a reply with the wrong sequence number', () => {
    const s = sent(watching());
    assert.equal(reduceSession(s, { type: 'heartbeat-reply', generation: 1, seq: 99, result: { ok: true, view: view() } }), s);
  });

  it('ignores a reply from an older generation', () => {
    const s = sent(watching());
    const later = reduceSession(sent(reduceSession(s, onPage({ generation: 2, videoId: B }))), { type: 'heartbeat-reply', generation: 1, seq: 1, result: { ok: true, view: view() } });
    assert.equal(later.view, null);
  });

  it('ignores the old reply even if its sequence number is reused by nobody', () => {
    const s = reduceSession(sent(watching()), onPage({ generation: 2, videoId: B }));
    assert.equal(reduceSession(s, { type: 'heartbeat-reply', generation: 1, seq: 1, result: { ok: true, view: view() } }), s);
  });

  it('ignores a reply with nothing in flight', () => {
    const s = watching();
    assert.equal(reduceSession(s, { type: 'heartbeat-reply', generation: 1, seq: 1, result: { ok: true, view: view() } }), s);
  });

  it('ignores a second copy of the same reply', () => {
    const s = answered(watching());
    assert.equal(reduceSession(s, { type: 'heartbeat-reply', generation: 1, seq: 1, result: { ok: true, view: view({ hits: [] }) } }), s);
  });

  it('keeps the hits across an unchanged reply', () => {
    assert.deepEqual(answered(answered(watching()), unchanged()).hits, [HIT]);
  });

  it('keeps the very same hits array across an unchanged reply', () => {
    const s = answered(watching());
    assert.equal(answered(s, unchanged()).hits, s.hits);
  });

  it('takes the new state from an unchanged reply', () => {
    assert.equal(answered(answered(watching()), unchanged({ state: 'complete' })).view.state, 'complete');
  });

  it('takes the new coverage from an unchanged reply', () => {
    assert.deepEqual(answered(answered(watching()), unchanged({ coverage: [[0, 90]] })).view.coverage, [[0, 90]]);
  });

  it('keeps since across an unchanged reply', () => {
    const s = answered(answered(watching()), unchanged());
    assert.deepEqual([s.session, s.revision], ['s1', 1]);
  });

  it('drops an unchanged reply from another session and forgets since', () => {
    const s = answered(answered(watching()), unchanged({ session: 's2' }));
    assert.deepEqual([s.session, s.revision], [null, null]);
  });

  it('keeps the old view for an unusable unchanged reply', () => {
    const s = answered(answered(watching()), unchanged({ session: 's2', state: 'complete' }));
    assert.equal(s.view.state, 'transcribing');
  });

  it('drops an unchanged reply when no hits are held', () => {
    const s = answered(watching(), unchanged());
    assert.deepEqual([s.view, s.hits, s.revision], [null, null, null]);
  });

  it('counts an unusable unchanged reply as an answer, not an error', () => {
    assert.equal(answered(errored(watching()), unchanged()).consecutiveErrors, 0);
  });

  it('replaces the hits on a new session', () => {
    const s = answered(answered(watching()), view({ session: 's2', revision: 1, hits: [{ start: 50, end: 51, phrase: 'y' }] }));
    assert.deepEqual(s.hits, [{ start: 50, end: 51, phrase: 'y' }]);
  });

  it('drops the hits on a new session with none yet', () => {
    assert.deepEqual(answered(answered(watching()), view({ session: 's2', hits: [] })).hits, []);
  });

  it('takes a newer revision of the same session', () => {
    assert.equal(answered(answered(watching()), view({ revision: 2 })).revision, 2);
  });

  it('treats a null hit list on a full reply as none', () => {
    assert.deepEqual(answered(watching(), view({ hits: null })).hits, []);
  });

  it('refuses a view of another video as a bad response', () => {
    assert.equal(answered(watching(), view({ videoId: B })).lastError.kind, 'bad_response');
  });

  it('keeps no view of another video', () => {
    assert.equal(answered(watching(), view({ videoId: B })).view, null);
  });

  it('records a failure', () => {
    assert.equal(errored(watching()).lastError.kind, 'unreachable');
  });

  it('counts failures in a row', () => {
    assert.equal(failTimes(watching(), 3).consecutiveErrors, 3);
  });

  it('keeps the view through a failure', () => {
    assert.notEqual(errored(answered(watching())).view, null);
  });

  it('keeps the hits through a failure', () => {
    assert.deepEqual(errored(answered(watching())).hits, [HIT]);
  });

  it('keeps since through a failure', () => {
    assert.equal(errored(answered(watching())).revision, 1);
  });

  it('clears the heartbeat in flight on a failure', () => {
    assert.equal(errored(watching()).inflight, null);
  });

  it('resets the error count on a good reply', () => {
    assert.equal(answered(failTimes(watching(), 2)).consecutiveErrors, 0);
  });

  it('clears the last error on a good reply', () => {
    assert.equal(answered(failTimes(watching(), 2)).lastError, null);
  });

  it('turns a malformed reply into an extension error', () => {
    assert.equal(reply(sent(watching()), { nope: 1 }).lastError.kind, 'extension');
  });

  it('turns a missing reply into an extension error', () => {
    assert.equal(reply(sent(watching()), undefined).lastError.kind, 'extension');
  });

  it('keeps the error kind and detail it is given', () => {
    const s = reply(sent(watching()), { ok: false, error: { kind: 'http', status: 503, detail: 'down' } });
    assert.deepEqual(s.lastError, { kind: 'http', status: 503, detail: 'down' });
  });
});

describe('session: tick', () => {
  it('keeps a heartbeat that is not yet late', () => {
    const s = sent(watching(), 1000);
    assert.equal(reduceSession(s, { type: 'tick', at: 1000 + INFLIGHT_TIMEOUT_MS - 1 }), s);
  });

  it('gives up a heartbeat at the timeout', () => {
    assert.equal(reduceSession(sent(watching(), 1000), { type: 'tick', at: 1000 + INFLIGHT_TIMEOUT_MS }).inflight, null);
  });

  it('records a timeout error for it', () => {
    const s = reduceSession(sent(watching(), 1000), { type: 'tick', at: 1000 + INFLIGHT_TIMEOUT_MS });
    assert.equal(s.lastError.kind, 'timeout');
  });

  it('counts it as a failure in a row', () => {
    const s = reduceSession(sent(errored(watching()), 5000), { type: 'tick', at: 5000 + INFLIGHT_TIMEOUT_MS });
    assert.equal(s.consecutiveErrors, 2);
  });

  it('ignores the late reply afterwards', () => {
    const s = reduceSession(sent(watching(), 1000), { type: 'tick', at: 1000 + INFLIGHT_TIMEOUT_MS });
    assert.equal(reduceSession(s, { type: 'heartbeat-reply', generation: 1, seq: 1, result: { ok: true, view: view() } }), s);
  });

  it('does nothing with nothing in flight', () => {
    const s = watching();
    assert.equal(reduceSession(s, { type: 'tick', at: 1e9 }), s);
  });

  it('does nothing without a time', () => {
    const s = sent(watching());
    assert.equal(reduceSession(s, { type: 'tick' }), s);
  });
});

describe('session: whether to heartbeat', () => {
  it('does while watching with the settings loaded', () => {
    assert.equal(shouldHeartbeat(watching()), true);
  });

  it('does not before the settings are loaded', () => {
    assert.equal(shouldHeartbeat(run(onPage())), false);
  });

  it('does not when disabled', () => {
    assert.equal(shouldHeartbeat(run(loaded({ enabled: false }), onPage())), false);
  });

  it('does not while idle', () => {
    assert.equal(shouldHeartbeat(run(loaded(), onPage({ phase: 'idle', videoId: null }))), false);
  });

  it('does not while leaving', () => {
    assert.equal(shouldHeartbeat(run(loaded(), onPage({ phase: 'leaving', videoId: null }))), false);
  });

  it('does not once unfiltered', () => {
    assert.equal(shouldHeartbeat(reduceSession(watching(), { type: 'unfiltered' })), false);
  });

  it('does not after the audio failed', () => {
    assert.equal(shouldHeartbeat(reduceSession(watching(), audioFailed)), false);
  });

  it('does again once the audio attaches after a failure', () => {
    assert.equal(shouldHeartbeat(run(loaded(), onPage(), audioFailed, { type: 'audio-detached' }, audioRunning)), true);
  });

  it('does while the audio is suspended', () => {
    assert.equal(shouldHeartbeat(reduceSession(watching(), audioSuspended)), true);
  });

  it('does during an ad (the session prepares meanwhile)', () => {
    assert.equal(shouldHeartbeat(watching({ adShowing: true })), true);
  });

  it('does before the element is found', () => {
    assert.equal(shouldHeartbeat(watching({ hasVideoElement: false })), true);
  });

  it('does after server errors', () => {
    assert.equal(shouldHeartbeat(failTimes(watching(), 5)), true);
  });

  it('does again when re-enabled', () => {
    assert.equal(shouldHeartbeat(run(loaded({ enabled: false }), onPage(), loaded())), true);
  });
});

describe('session: heartbeat cadence', () => {
  it('is 1 s before any view', () => {
    assert.equal(heartbeatCadence(watching()), HEARTBEAT_MS);
  });

  for (const state of ['queued', 'fetching', 'preparing', 'cancelled']) {
    it(`is 250 ms while ${state} (W17)`, () => {
      assert.equal(heartbeatCadence(answered(watching(), view({ state }))), FAST_HEARTBEAT_MS);
    });
  }

  it('is 1 s while transcribing with enough covered ahead', () => {
    assert.equal(heartbeatCadence(answered(watching(), view({ state: 'transcribing' }))), HEARTBEAT_MS);
  });

  for (const state of ['complete', 'failed', 'unsupported']) {
    it(`is 5 s once ${state}`, () => {
      assert.equal(heartbeatCadence(answered(watching(), view({ state }))), SETTLED_HEARTBEAT_MS);
    });
  }

  it('stays 1 s while errors keep coming before a view', () => {
    assert.equal(heartbeatCadence(failTimes(watching(), 4)), HEARTBEAT_MS);
  });

  it('keeps the settled cadence through errors after completion', () => {
    assert.equal(heartbeatCadence(errored(answered(watching(), view({ state: 'complete' })))), SETTLED_HEARTBEAT_MS);
  });
});

describe('session: when the next heartbeat is due', () => {
  it('is at once for a fresh generation', () => {
    assert.equal(nextHeartbeatAt(watching()), 0);
  });

  it('is never when heartbeats are off', () => {
    assert.equal(nextHeartbeatAt(run(loaded({ enabled: false }), onPage())), null);
  });

  it('is never while idle', () => {
    assert.equal(nextHeartbeatAt(run(loaded(), onPage({ phase: 'idle', videoId: null }))), null);
  });

  it('is never once unfiltered', () => {
    assert.equal(nextHeartbeatAt(reduceSession(answered(watching()), { type: 'unfiltered' })), null);
  });

  it('is the give-up time while one is in flight', () => {
    assert.equal(nextHeartbeatAt(sent(watching(), 1000)), 1000 + INFLIGHT_TIMEOUT_MS);
  });

  it('is a second after the last one while working', () => {
    assert.equal(nextHeartbeatAt(answered(watching(), view(), 1000)), 2000);
  });

  it('is five seconds after the last one once complete', () => {
    assert.equal(nextHeartbeatAt(answered(watching(), view({ state: 'complete' }), 1000)), 6000);
  });

  it('is a second after a failed one', () => {
    assert.equal(nextHeartbeatAt(errored(watching(), 'unreachable', 1000)), 2000);
  });

  it('is at once after a navigation', () => {
    assert.equal(nextHeartbeatAt(reduceSession(answered(watching()), onPage({ generation: 2, videoId: B }))), 0);
  });

  it('is due at once for a fresh generation', () => {
    assert.equal(heartbeatDue(watching(), 0), true);
  });

  it('is not due while one is in flight', () => {
    assert.equal(heartbeatDue(sent(watching(), 1000), 1e9), false);
  });

  it('is not due before the cadence', () => {
    assert.equal(heartbeatDue(answered(watching(), view(), 1000), 1999), false);
  });

  it('is due at the cadence', () => {
    assert.equal(heartbeatDue(answered(watching(), view(), 1000), 2000), true);
  });

  it('is not due when heartbeats are off', () => {
    assert.equal(heartbeatDue(run(loaded({ enabled: false }), onPage()), 1e9), false);
  });

  it('is due again after a lost heartbeat is given up', () => {
    const s = reduceSession(sent(watching(), 1000), { type: 'tick', at: 11_000 });
    assert.equal(heartbeatDue(s, 11_000), true);
  });
});

describe('session: the heartbeat request', () => {
  it('names the video on YouTube', () => {
    const r = heartbeatRequest(watching(), media());
    assert.deepEqual([r.provider, r.videoId], ['youtube', A]);
  });

  it('carries the playhead', () => {
    assert.equal(heartbeatRequest(watching(), media({ position: 33.5 })).position, 33.5);
  });

  it('has no since before the first view', () => {
    const r = heartbeatRequest(watching(), media());
    assert.deepEqual([r.since, r.session], [null, null]);
  });

  it('carries since and session after a view', () => {
    const r = heartbeatRequest(answered(watching(), view({ session: 'abc', revision: 5 })), media());
    assert.deepEqual([r.since, r.session], [5, 'abc']);
  });

  it('drops since after an unusable unchanged reply', () => {
    const r = heartbeatRequest(answered(answered(watching()), unchanged({ session: 'other' })), media());
    assert.deepEqual([r.since, r.session], [null, null]);
  });

  it('reports the playhead from before an ad during the ad', () => {
    const s = reduceSession(sent(watching(), 1000, 42), onPage({ adShowing: true }));
    assert.equal(heartbeatRequest(s, media({ position: 3 })).position, 42);
  });

  it('reports 0 during an ad before any heartbeat', () => {
    assert.equal(heartbeatRequest(watching({ adShowing: true }), media({ position: 3 })).position, 0);
  });

  it('reports the last playhead with no element', () => {
    assert.equal(heartbeatRequest(sent(watching(), 1000, 7), null).position, 7);
  });

  it('reports the last playhead for a NaN currentTime', () => {
    assert.equal(heartbeatRequest(sent(watching(), 1000, 7), media({ position: NaN })).position, 7);
  });

  it('is null when heartbeats are off', () => {
    assert.equal(heartbeatRequest(run(loaded({ enabled: false }), onPage()), media()), null);
  });

  it('is null while idle', () => {
    assert.equal(heartbeatRequest(run(loaded(), onPage({ phase: 'idle', videoId: null })), media()), null);
  });
});

describe('session: error tolerance', () => {
  it('shows no error with none', () => {
    assert.equal(gateError(answered(watching()), media()), null);
  });

  it('shows the first error at once when there is no view', () => {
    assert.equal(gateError(errored(watching()), media()).kind, 'unreachable');
  });

  it('hides one error while comfortably covered', () => {
    assert.equal(gateError(errored(answered(watching())), media({ position: 0 })), null);
  });

  it('hides two errors while comfortably covered', () => {
    assert.equal(gateError(failTimes(answered(watching()), 2), media({ position: 0 })), null);
  });

  it('shows the third error in a row', () => {
    assert.equal(gateError(failTimes(answered(watching()), ERROR_TOLERANCE), media()).kind, 'unreachable');
  });

  it('shows one error at once when coverage ahead is under the hold threshold', () => {
    assert.equal(gateError(errored(answered(watching())), media({ position: 40 })).kind, 'unreachable');
  });

  it('hides one error with exactly the hold threshold ahead', () => {
    assert.equal(gateError(errored(answered(watching())), media({ position: 38 })), null);
  });

  it('scales the threshold with the rate', () => {
    assert.equal(gateError(errored(answered(watching())), media({ position: 32, playbackRate: 2 })).kind, 'unreachable');
  });

  it('shows one error at once when the playhead is outside coverage', () => {
    assert.equal(gateError(errored(answered(watching())), media({ position: 100 })).kind, 'unreachable');
  });

  it('hides one error once complete', () => {
    assert.equal(gateError(errored(answered(watching(), view({ state: 'complete' }))), media({ position: 600 })), null);
  });

  it('forgets errors after a good reply', () => {
    assert.equal(gateError(answered(failTimes(answered(watching()), 3)), media()), null);
  });

  it('shows the audio error with no server trouble', () => {
    assert.equal(gateError(reduceSession(answered(watching()), audioFailed), media()), AUDIO_ERROR);
  });

  it('prefers the audio error to a server error', () => {
    assert.equal(gateError(reduceSession(failTimes(watching(), 3), audioFailed), media()).kind, 'audio');
  });

  it('shows a lost heartbeat as a timeout when there is no view', () => {
    const s = reduceSession(sent(watching(), 1000), { type: 'tick', at: 11_000 });
    assert.equal(gateError(s, media()).kind, 'timeout');
  });
});

const GATE_INPUT_KEYS = [
  'page',
  'view',
  'error',
  'unfiltered',
  'failPolicy',
  'position',
  'playbackRate',
  'duration',
  'paused',
  'ended',
  'heldByUs',
  'userPaused',
  'playRequested',
  'serverUrl',
].sort();

describe('session: the gate input (W13 contract)', () => {
  it('has exactly the W13 fields', () => {
    assert.deepEqual(Object.keys(gateInput(watching(), media())).sort(), GATE_INPUT_KEYS);
  });

  it('passes the page state', () => {
    const s = watching();
    assert.equal(gateInput(s, media()).page, s.page);
  });

  it('passes the view', () => {
    const s = answered(watching());
    assert.equal(gateInput(s, media()).view, s.view);
  });

  it('passes a null view before the first answer', () => {
    assert.equal(gateInput(watching(), media()).view, null);
  });

  it('passes the element facts through', () => {
    const m = media({ position: 5, playbackRate: 1.5, duration: 100, paused: true, ended: false });
    const i = gateInput(watching(), m);
    assert.deepEqual([i.position, i.playbackRate, i.duration, i.paused, i.ended], [5, 1.5, 100, true, false]);
  });

  it('passes the controller facts through', () => {
    const i = gateInput(watching(), media({ heldByUs: true, userPaused: true, playRequested: true }));
    assert.deepEqual([i.heldByUs, i.userPaused, i.playRequested], [true, true, true]);
  });

  it('passes the fail policy', () => {
    assert.equal(gateInput(watching(), media()).failPolicy, 'closed');
  });

  it('passes the server URL for error sentences', () => {
    assert.equal(gateInput(run(loaded({ serverUrl: 'http://127.0.0.1:9000' }), onPage()), media()).serverUrl, 'http://127.0.0.1:9000');
  });

  it('is not unfiltered by default', () => {
    assert.equal(gateInput(watching(), media()).unfiltered, false);
  });

  it('is unfiltered after the click', () => {
    assert.equal(gateInput(reduceSession(watching(), { type: 'unfiltered' }), media()).unfiltered, true);
  });

  it('is unfiltered when disabled', () => {
    assert.equal(gateInput(run(loaded({ enabled: false }), onPage()), media()).unfiltered, true);
  });

  it('uses a stopped element with no media', () => {
    const i = gateInput(watching(), null);
    assert.deepEqual([i.position, i.paused, i.heldByUs], [NO_MEDIA.position, true, false]);
  });

  it('passes the surfaced error', () => {
    assert.equal(gateInput(errored(watching()), media()).error.kind, 'unreachable');
  });

  it('passes a null error when it is tolerated', () => {
    assert.equal(gateInput(errored(answered(watching())), media()).error, null);
  });
});

/** decideGate on the derived input, as the content script applies it. */
const decide = (state, m = media()) => deriveSession(state, m).gate;

describe('session: end to end with decideGate', () => {
  it('holds a first view before any answer (starting)', () => {
    assert.deepEqual([decide(watching()).action, decide(watching()).reason], ['hold', 'starting']);
  });

  it('holds while the service is fetching (preparing)', () => {
    const s = answered(watching(), view({ state: 'fetching', coverage: [], hits: [] }));
    assert.equal(decide(s).reason, 'preparing');
  });

  it('holds while the first window covers too little', () => {
    const s = answered(watching(), view({ coverage: [[0, 24]] }));
    assert.equal(decide(s, media({ heldByUs: true, paused: true })).action, 'hold');
  });

  it('releases once enough is covered, playing again', () => {
    const s = answered(watching(), view({ coverage: [[0, 46]] }));
    const d = decide(s, media({ heldByUs: true, paused: true }));
    assert.deepEqual([d.action, d.resume], ['release', true]);
  });

  it('plays straight through a cached (complete) video', () => {
    const s = answered(watching(), view({ state: 'complete', fromCache: true, coverage: [[0, 646.3]] }));
    assert.equal(decide(s, media({ position: 648 })).action, 'none');
  });

  it('keeps playing through two server errors while covered', () => {
    const s = failTimes(answered(watching()), 2);
    assert.equal(decide(s, media({ position: 5 })).action, 'none');
  });

  it('holds with the error once the server is down for three heartbeats mid-transcription', () => {
    const d = decide(failTimes(answered(watching()), 3), media({ position: 5 }));
    assert.deepEqual([d.action, d.reason], ['hold', 'error']);
  });

  it('says the service is not reachable in the overlay', () => {
    const o = deriveSession(failTimes(answered(watching()), 3), media({ position: 5 })).overlay;
    assert.equal(o.title, 'FoulFilterNet is not reachable');
  });

  it('holds with the error at once when the server is down and coverage runs short', () => {
    const d = decide(errored(answered(watching())), media({ position: 40 }));
    assert.deepEqual([d.action, d.reason], ['hold', 'error']);
  });

  it('holds with the error when the very first heartbeat fails', () => {
    assert.equal(decide(errored(watching())).reason, 'error');
  });

  it('keeps playing a complete video with the server down', () => {
    const s = failTimes(answered(watching(), view({ state: 'complete' })), 5);
    assert.equal(decide(s, media({ position: 300 })).action, 'none');
  });

  it('releases a held complete video with the server down', () => {
    const s = failTimes(answered(watching(), view({ state: 'complete' })), 5);
    assert.equal(decide(s, media({ heldByUs: true, paused: true })).action, 'release');
  });

  it('recovers when the server comes back', () => {
    const s = answered(failTimes(answered(watching()), 3), view({ revision: 2 }));
    assert.equal(decide(s, media({ heldByUs: true, paused: true })).action, 'release');
  });

  it('holds a failed video with the failure reason', () => {
    const s = answered(watching(), view({ state: 'failed', coverage: [], hits: [], failureKind: 'unavailable' }));
    assert.equal(decide(s).reason, 'failed');
  });

  it('holds an unsupported video', () => {
    const s = answered(watching(), view({ state: 'unsupported', coverage: [], hits: [] }));
    assert.equal(decide(s).reason, 'unsupported');
  });

  it('holds when the service cannot keep up', () => {
    const s = answered(watching(), view({ coverage: [[0, 5]], keepingUp: false }));
    assert.equal(decide(s).reason, 'not-keeping-up');
  });

  it('holds with the audio error when the element cannot be routed', () => {
    const d = decide(reduceSession(answered(watching(), view({ state: 'complete' })), audioFailed));
    assert.deepEqual([d.action, d.reason], ['hold', 'error']);
  });

  it('lets go when unfiltered', () => {
    const s = reduceSession(errored(watching()), { type: 'unfiltered' });
    assert.equal(decide(s, media({ heldByUs: true, paused: true })).action, 'release');
  });

  it('lets go when disabled', () => {
    assert.equal(decide(run(loaded({ enabled: false }), onPage()), media({ heldByUs: true, paused: true })).action, 'release');
  });

  it('stands aside for an ad', () => {
    assert.equal(decide(watching({ adShowing: true })).reason, 'ad');
  });

  it('stands aside when leaving', () => {
    assert.equal(decide(run(loaded(), onPage({ phase: 'leaving', videoId: null }))).reason, 'leaving');
  });

  it('stands aside while idle', () => {
    assert.equal(decide(run(loaded(), onPage({ phase: 'idle', videoId: null }))).action, 'none');
  });

  it('holds a navigation back to a video until the new generation answers', () => {
    const s = reduceSession(answered(watching(), view({ state: 'complete' })), onPage({ generation: 2 }));
    assert.equal(decide(s).reason, 'starting');
  });
});

describe('session: suspended audio', () => {
  const suspended = (state = answered(watching(), view({ state: 'complete' }))) => reduceSession(state, audioSuspended);

  it('holds the video', () => {
    assert.equal(decide(suspended()).action, 'hold');
  });

  it('holds with the reason audio-suspended', () => {
    assert.equal(decide(suspended()).reason, 'audio-suspended');
  });

  it('holds even when the gate would let it play', () => {
    assert.equal(decide(suspended(), media({ position: 300 })).action, 'hold');
  });

  it('holds even when unfiltered (the context still carries the sound)', () => {
    assert.equal(decide(reduceSession(suspended(), { type: 'unfiltered' })).action, 'hold');
  });

  it('asks for a click in the overlay', () => {
    assert.equal(deriveSession(suspended(), media()).overlay.title, 'Click to enable FoulFilter');
  });

  it('shows the Enable sound button', () => {
    assert.equal(deriveSession(suspended(), media()).overlay.showEnable, true);
  });

  it('still offers Watch unfiltered', () => {
    assert.equal(deriveSession(suspended(), media()).overlay.showUnfiltered, true);
  });

  it('does not hold during an ad', () => {
    assert.equal(decide(reduceSession(suspended(), onPage({ adShowing: true }))).action, 'none');
  });

  it('does not hold an ended video', () => {
    assert.equal(decide(suspended(), media({ ended: true })).action, 'none');
  });

  it('does not hold when disabled', () => {
    assert.equal(decide(reduceSession(suspended(), loaded({ enabled: false }))).action, 'none');
  });

  it('does not hold while idle', () => {
    assert.equal(decide(reduceSession(suspended(), onPage({ phase: 'idle', videoId: null }))).action, 'none');
  });

  it('lets go once the context runs', () => {
    const d = decide(reduceSession(suspended(), audioRunning), media({ heldByUs: true, paused: true }));
    assert.equal(d.action, 'release');
  });

  it('shows a problem badge', () => {
    assert.equal(deriveSession(suspended(), media()).badge.text, '!');
  });
});

describe('session: censor mode', () => {
  const mode = (state, m = media()) => deriveSession(state, m).mode;
  const covered = () => answered(watching());

  it('filters a covered video that is playing', () => {
    assert.equal(mode(covered()), 'filter');
  });

  it('filters a complete video', () => {
    assert.equal(mode(answered(watching(), view({ state: 'complete' }))), 'filter');
  });

  it('mutes while the gate holds', () => {
    assert.equal(mode(watching()), 'mute');
  });

  it('mutes while holding for an error', () => {
    assert.equal(mode(failTimes(covered(), 3)), 'mute');
  });

  it('mutes while the audio failed', () => {
    assert.equal(mode(reduceSession(covered(), audioFailed)), 'mute');
  });

  it('mutes while leaving', () => {
    assert.equal(mode(run(loaded(), onPage({ phase: 'leaving', videoId: null }))), 'mute');
  });

  it('mutes while idle (the miniplayer)', () => {
    assert.equal(mode(run(loaded(), onPage({ phase: 'idle', videoId: null }))), 'mute');
  });

  it('mutes while idle even when paused', () => {
    assert.equal(mode(run(loaded(), onPage({ phase: 'idle', videoId: null })), media({ paused: true })), 'mute');
  });

  it('mutes with no element', () => {
    assert.equal(mode(answered(watching({ hasVideoElement: false }), view({ state: 'complete' }))), 'mute');
  });

  it('opens for an ad', () => {
    assert.equal(mode(reduceSession(covered(), onPage({ adShowing: true }))), 'open');
  });

  it('opens when unfiltered', () => {
    assert.equal(mode(reduceSession(covered(), { type: 'unfiltered' })), 'open');
  });

  it('opens when unfiltered even while an error would hold', () => {
    assert.equal(mode(reduceSession(errored(watching()), { type: 'unfiltered' })), 'open');
  });

  it('opens when disabled', () => {
    assert.equal(mode(run(loaded({ enabled: false }), onPage())), 'open');
  });

  it('opens when disabled even while idle', () => {
    assert.equal(mode(run(loaded({ enabled: false }), onPage({ phase: 'idle', videoId: null }))), 'open');
  });

  it('keeps filtering through tolerated errors', () => {
    assert.equal(mode(failTimes(covered(), 2)), 'filter');
  });

  it('keeps filtering a complete video with the server down', () => {
    assert.equal(mode(failTimes(answered(watching(), view({ state: 'complete' })), 5)), 'filter');
  });
});

describe('session: hits, method and offset', () => {
  it('has no hits before a view', () => {
    assert.deepEqual(deriveSession(watching(), media()).hits, []);
  });

  it('keeps the same empty array while nothing is known', () => {
    assert.equal(deriveSession(watching(), media()).hits, deriveSession(run(loaded()), media()).hits);
  });

  it('has the view hits', () => {
    assert.deepEqual(deriveSession(answered(watching()), media()).hits, [HIT]);
  });

  it('keeps the hits array identical across an unchanged reply', () => {
    const s = answered(watching());
    assert.equal(deriveSession(answered(s, unchanged()), media()).hits, deriveSession(s, media()).hits);
  });

  it('has no hits after a navigation', () => {
    assert.deepEqual(deriveSession(reduceSession(answered(watching()), onPage({ generation: 2, videoId: B })), media()).hits, []);
  });

  it('uses silence by default', () => {
    assert.equal(deriveSession(watching(), media()).method, 'silence');
  });

  it('uses bleep when set', () => {
    assert.equal(deriveSession(run(loaded({ censorMethod: 'bleep' }), onPage()), media()).method, 'bleep');
  });

  it('passes the offset', () => {
    assert.equal(deriveSession(run(loaded({ offsetMs: -120 }), onPage()), media()).offsetMs, -120);
  });

  it('follows a settings change', () => {
    assert.equal(deriveSession(reduceSession(watching(), loaded({ offsetMs: 80 })), media()).offsetMs, 80);
  });
});

describe('session: the miniplayer', () => {
  const idle = (...extra) => run(loaded(), onPage(), audioRunning, onPage({ phase: 'idle', videoId: null }), ...extra);

  it('is muted when playing on a non-watch page', () => {
    assert.equal(isMiniplayerMuted(idle(), media()), true);
  });

  it('is reported by deriveSession', () => {
    assert.equal(deriveSession(idle(), media()).miniplayer, true);
  });

  it('mutes the censor', () => {
    assert.equal(deriveSession(idle(), media()).mode, 'mute');
  });

  it('says so in the overlay', () => {
    assert.equal(deriveSession(idle(), media()).overlay.title, 'FoulFilter muted the miniplayer');
  });

  it('offers no Watch unfiltered button for it', () => {
    assert.equal(deriveSession(idle(), media()).overlay.showUnfiltered, false);
  });

  it('says so in the badge', () => {
    assert.equal(deriveSession(idle(), media()).badge.text, 'MUTE');
  });

  it('colours the badge as muted', () => {
    assert.equal(deriveSession(idle(), media()).badge.color, BADGE_COLORS.muted);
  });

  it('does not hold it', () => {
    assert.equal(deriveSession(idle(), media()).gate.action, 'none');
  });

  it('is not reported while paused', () => {
    assert.equal(isMiniplayerMuted(idle(), media({ paused: true })), false);
  });

  it('is not reported once ended', () => {
    assert.equal(isMiniplayerMuted(idle(), media({ ended: true })), false);
  });

  it('is not reported when the element never went through the graph', () => {
    assert.equal(isMiniplayerMuted(run(loaded(), onPage({ phase: 'idle', videoId: null })), media()), false);
  });

  it('is not reported with no element', () => {
    assert.equal(isMiniplayerMuted(idle(onPage({ phase: 'idle', videoId: null, hasVideoElement: false })), media()), false);
  });

  it('is not reported when disabled', () => {
    assert.equal(isMiniplayerMuted(idle(loaded({ enabled: false })), media()), false);
  });

  it('is not reported while watching', () => {
    assert.equal(isMiniplayerMuted(reduceSession(watching(), audioRunning), media()), false);
  });

  it('is not reported while leaving', () => {
    assert.equal(isMiniplayerMuted(idle(onPage({ phase: 'leaving', videoId: null })), media()), false);
  });

  it('is reported with a suspended context too', () => {
    assert.equal(isMiniplayerMuted(idle(audioSuspended), media()), true);
  });
});

describe('session: overlay', () => {
  const overlay = (state, m = media()) => deriveSession(state, m).overlay;

  it('is hidden when disabled', () => {
    assert.equal(overlay(run(loaded({ enabled: false }), onPage())), HIDDEN_OVERLAY);
  });

  it('is hidden when disabled even with the audio failed', () => {
    assert.equal(overlay(run(loaded({ enabled: false }), onPage(), audioFailed)).visible, false);
  });

  it('shows the gate reason during a hold', () => {
    assert.equal(overlay(watching()).title, 'FoulFilter is starting');
  });

  it('shows the gate percent while transcribing', () => {
    const s = answered(watching(), view({ coverage: [[0, 15]] }));
    assert.equal(overlay(s, media({ heldByUs: true, paused: true })).percent, 50);
  });

  it('is hidden when playing covered', () => {
    assert.equal(overlay(answered(watching())).visible, false);
  });

  it('is hidden while idle and paused', () => {
    assert.equal(overlay(run(loaded(), onPage({ phase: 'idle', videoId: null })), media({ paused: true })).visible, false);
  });

  it('is hidden for an ad', () => {
    assert.equal(overlay(watching({ adShowing: true })).visible, false);
  });

  it('explains an audio failure with a reload', () => {
    assert.match(overlay(reduceSession(watching(), audioFailed)).detail, /Reload the page/);
  });

  it('is the gate overlay exactly for a gate hold', () => {
    const d = deriveSession(errored(watching()), media());
    assert.deepEqual(d.overlay, gateOverlay(d.input, d.gate));
  });

  it('applies the decision decideGate makes from the input', () => {
    const d = deriveSession(errored(watching()), media());
    assert.deepEqual(d.gate, decideGate(d.input));
  });
});

describe('session: badge', () => {
  const badge = (state, m = media()) => deriveSession(state, m).badge;

  it('says OFF when disabled', () => {
    assert.equal(badge(run(loaded({ enabled: false }), onPage())).text, 'OFF');
  });

  it('is grey when disabled', () => {
    assert.equal(badge(run(loaded({ enabled: false }), onPage())).color, BADGE_COLORS.off);
  });

  it('is empty on a non-watch page', () => {
    assert.equal(badge(run(loaded(), onPage({ phase: 'idle', videoId: null })), media({ paused: true })).text, '');
  });

  it('is empty while leaving', () => {
    assert.equal(badge(run(loaded(), onPage({ phase: 'leaving', videoId: null }))).text, '');
  });

  it('says … while starting', () => {
    assert.equal(badge(watching()).text, '…');
  });

  it('is blue while preparing', () => {
    assert.equal(badge(watching()).color, BADGE_COLORS.preparing);
  });

  it('carries the overlay title in its tooltip while preparing', () => {
    assert.match(badge(watching()).title, /FoulFilter is starting/);
  });

  it('carries the percent in its tooltip', () => {
    const s = answered(watching(), view({ coverage: [[0, 15]] }));
    assert.match(badge(s, media({ heldByUs: true, paused: true })).title, /\(50%\)/);
  });

  it('says ✓ while filtering', () => {
    assert.equal(badge(answered(watching())).text, '✓');
  });

  it('is green while filtering', () => {
    assert.equal(badge(answered(watching())).color, BADGE_COLORS.filtering);
  });

  it('says how much is prepared while still transcribing', () => {
    assert.match(badge(answered(watching(), view({ progress: 0.25 }))).title, /25% prepared/);
  });

  it('says nothing about preparation once complete', () => {
    assert.doesNotMatch(badge(answered(watching(), view({ state: 'complete' }))).title, /prepared/);
  });

  it('says ✓ for a complete video with the server down', () => {
    assert.equal(badge(failTimes(answered(watching(), view({ state: 'complete' })), 5)).text, '✓');
  });

  it('says ! for a surfaced server error', () => {
    assert.equal(badge(errored(watching())).text, '!');
  });

  it('is red for a surfaced server error', () => {
    assert.equal(badge(errored(watching())).color, BADGE_COLORS.problem);
  });

  it('says ! for a failed video', () => {
    assert.equal(badge(answered(watching(), view({ state: 'failed', coverage: [], hits: [] }))).text, '!');
  });

  it('says ! for an unsupported video', () => {
    assert.equal(badge(answered(watching(), view({ state: 'unsupported', coverage: [], hits: [] }))).text, '!');
  });

  it('says ! when the service cannot keep up', () => {
    assert.equal(badge(answered(watching(), view({ coverage: [[0, 5]], keepingUp: false }))).text, '!');
  });

  it('says ! for an audio failure', () => {
    assert.equal(badge(reduceSession(answered(watching()), audioFailed)).text, '!');
  });

  it('says OFF when watching unfiltered', () => {
    assert.equal(badge(reduceSession(watching(), { type: 'unfiltered' })).text, 'OFF');
  });

  it('says why in its tooltip when watching unfiltered', () => {
    assert.match(badge(reduceSession(watching(), { type: 'unfiltered' })).title, /unfiltered/);
  });

  it('says AD during an ad', () => {
    assert.equal(badge(watching({ adShowing: true })).text, 'AD');
  });

  it('says … before the element is found', () => {
    assert.equal(badge(answered(watching({ hasVideoElement: false }), view({ state: 'complete' }))).text, '…');
  });

  it('stays ✓ through tolerated errors', () => {
    assert.equal(badge(failTimes(answered(watching()), 2)).text, '✓');
  });

  it('is at most four characters in every case above', () => {
    for (const text of ['OFF', '', '…', '✓', '!', 'AD', 'MUTE']) assert.ok([...text].length <= 4);
  });

  it('is frozen', () => {
    assert.ok(Object.isFrozen(badge(watching())));
  });
});

describe('session: fast heartbeats while starting up (W17)', () => {
  it('beats every 250 ms', () => {
    assert.equal(FAST_HEARTBEAT_MS, 250);
  });

  it('is faster than the working cadence', () => {
    assert.ok(FAST_HEARTBEAT_MS < HEARTBEAT_MS);
  });

  it('is not starting up before any view', () => {
    assert.equal(isStartingUp(watching()), false);
  });

  it('is starting up while queued', () => {
    assert.equal(isStartingUp(answered(watching(), view({ state: 'queued', coverage: [] }))), true);
  });

  it('is starting up while transcribing with less than the resume threshold ahead', () => {
    assert.equal(isStartingUp(answered(watching(), view({ coverage: [[0, 24]] }))), true);
  });

  it('is starting up while transcribing with nothing covered yet', () => {
    assert.equal(isStartingUp(answered(watching(), view({ coverage: [] }))), true);
  });

  it('is not once the resume threshold is covered', () => {
    assert.equal(isStartingUp(answered(watching(), view({ coverage: [[0, RESUME_SECONDS]] }))), false);
  });

  it('is just short of the resume threshold', () => {
    assert.equal(isStartingUp(answered(watching(), view({ coverage: [[0, RESUME_SECONDS - 0.01]] }))), true);
  });

  it('measures from the playhead last reported', () => {
    const s = reply(sent(watching(), 1000, 20), { ok: true, view: view({ coverage: [[0, 46]] }) });
    assert.equal(isStartingUp(s), true);
  });

  it('is not when the covered run reaches the end of the video', () => {
    const s = reply(sent(watching(), 1000, 630), { ok: true, view: view({ coverage: [[0, 648.8]], duration: 649 }) });
    assert.equal(isStartingUp(s), false);
  });

  it('is when the run stops short of the end', () => {
    const s = reply(sent(watching(), 1000, 630), { ok: true, view: view({ coverage: [[0, 647]], duration: 649 }) });
    assert.equal(isStartingUp(s), true);
  });

  it('is when the duration is not known yet', () => {
    const s = reply(sent(watching(), 1000, 630), { ok: true, view: view({ coverage: [[0, 648.8]], duration: null }) });
    assert.equal(isStartingUp(s), true);
  });

  for (const state of ['complete', 'failed', 'unsupported']) {
    it(`is not once ${state}, even with little covered`, () => {
      assert.equal(isStartingUp(answered(watching(), view({ state, coverage: [[0, 5]] }))), false);
    });

    it(`keeps the settled cadence once ${state}`, () => {
      assert.equal(heartbeatCadence(answered(watching(), view({ state, coverage: [] }))), SETTLED_HEARTBEAT_MS);
    });
  }

  it('is not after an error, however early', () => {
    assert.equal(isStartingUp(errored(answered(watching(), view({ state: 'fetching' })))), false);
  });

  it('falls back to 1 s after an error', () => {
    assert.equal(heartbeatCadence(errored(answered(watching(), view({ state: 'fetching' })))), HEARTBEAT_MS);
  });

  it('speeds up again after a good reply', () => {
    const s = answered(errored(answered(watching(), view({ state: 'fetching' }))), view({ state: 'fetching' }), 3000);
    assert.equal(heartbeatCadence(s), FAST_HEARTBEAT_MS);
  });

  it('keeps the fast cadence across an unchanged reply', () => {
    const first = answered(watching(), view({ state: 'transcribing', coverage: [[0, 24]] }));
    const s = answered(first, unchanged({ state: 'transcribing', coverage: [[0, 24]] }), 2000);
    assert.equal(heartbeatCadence(s), FAST_HEARTBEAT_MS);
  });

  it('makes the next heartbeat due 250 ms after the last', () => {
    assert.equal(nextHeartbeatAt(answered(watching(), view({ state: 'fetching' }), 1000)), 1250);
  });

  it('is not due at 249 ms', () => {
    assert.equal(heartbeatDue(answered(watching(), view({ state: 'fetching' }), 1000), 1249), false);
  });

  it('is due at 250 ms', () => {
    assert.equal(heartbeatDue(answered(watching(), view({ state: 'fetching' }), 1000), 1250), true);
  });
});

describe('session: a seek (W17)', () => {
  const seek = { type: 'seek' };

  it('marks a heartbeat as wanted at once', () => {
    assert.equal(reduceSession(answered(watching()), seek).seekPending, true);
  });

  it('starts with none wanted', () => {
    assert.equal(watching().seekPending, false);
  });

  it('is ignored when not watching', () => {
    const idle = run(loaded(), onPage({ phase: 'idle', videoId: null }));
    assert.equal(reduceSession(idle, seek), idle);
  });

  it('changes nothing when one is already wanted', () => {
    const s = reduceSession(answered(watching()), seek);
    assert.equal(reduceSession(s, seek), s);
  });

  it('makes the next heartbeat due now', () => {
    assert.equal(nextHeartbeatAt(reduceSession(answered(watching(), view(), 1000), seek)), 0);
  });

  it('makes a heartbeat due straight after the last one', () => {
    assert.equal(heartbeatDue(reduceSession(answered(watching(), view(), 1000), seek), 1001), true);
  });

  it('does so once settled too', () => {
    const s = reduceSession(answered(watching(), view({ state: 'complete' }), 1000), seek);
    assert.equal(heartbeatDue(s, 1001), true);
  });

  it('waits for the heartbeat in flight', () => {
    const s = reduceSession(sent(answered(watching(), view(), 1000), 1500), seek);
    assert.deepEqual([heartbeatDue(s, 1600), nextHeartbeatAt(s)], [false, 1500 + INFLIGHT_TIMEOUT_MS]);
  });

  it('is due as soon as the heartbeat in flight is answered', () => {
    const inFlight = reduceSession(sent(answered(watching(), view(), 1000), 1500), seek);
    const s = reply(inFlight, { ok: true, view: view() });
    assert.deepEqual([heartbeatDue(s, 1600), nextHeartbeatAt(s)], [true, 0]);
  });

  it('is cleared by the heartbeat that goes out', () => {
    const s = sent(reduceSession(answered(watching(), view(), 1000), seek), 1001, 300);
    assert.equal(s.seekPending, false);
  });

  it('returns to the cadence after that heartbeat is answered', () => {
    const s = answered(reduceSession(answered(watching(), view(), 1000), seek), view(), 1001);
    assert.equal(nextHeartbeatAt(s), 1001 + HEARTBEAT_MS);
  });

  it('is forgotten by a navigation', () => {
    const s = reduceSession(reduceSession(answered(watching()), seek), onPage({ generation: 2, videoId: B }));
    assert.equal(s.seekPending, false);
  });

  it('sends nothing when heartbeats are off', () => {
    const s = reduceSession(run(loaded({ enabled: false }), onPage()), seek);
    assert.deepEqual([nextHeartbeatAt(s), heartbeatDue(s, 1e9)], [null, false]);
  });

  it('sends nothing once unfiltered', () => {
    const s = reduceSession(reduceSession(answered(watching()), { type: 'unfiltered' }), seek);
    assert.equal(heartbeatDue(s, 1e9), false);
  });

  it('carries the new playhead', () => {
    const s = reduceSession(answered(watching(), view(), 1000), seek);
    assert.equal(heartbeatRequest(s, media({ position: 300 })).position, 300);
  });
});
