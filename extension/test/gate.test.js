import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  decideGate,
  effectiveRate,
  END_TOLERANCE,
  gateOverlay,
  HIDDEN_OVERLAY,
  HOLD_SECONDS,
  mediaAhead,
  RESUME_SECONDS,
} from '../src/gate.js';

const ID = 'dQw4w9WgXcQ';
const OTHER = 'jNQXAC9IVRw';
const SERVER = 'http://localhost:8000';

const WATCHING = Object.freeze({ phase: 'watching', videoId: ID, adShowing: false, hasVideoElement: true, generation: 1 });

/** A view of the video on the page; transcribing, nothing covered, unless overridden. */
function view(fields = {}) {
  return {
    key: `youtube-${ID}`,
    provider: 'youtube',
    videoId: ID,
    session: 'abc',
    state: 'transcribing',
    reason: null,
    failureKind: null,
    title: null,
    duration: 649,
    revision: 1,
    unchanged: false,
    coverage: [],
    hits: [],
    windowsDone: 0,
    windowsTotal: 30,
    progress: 0,
    realtimeFactor: null,
    keepingUp: true,
    fromCache: false,
    ...fields,
  };
}

/** A gate input: watching, playing at 1x from 100 s, not held, unless overridden. */
function input(fields = {}) {
  return {
    page: WATCHING,
    view: view(),
    error: null,
    unfiltered: false,
    failPolicy: 'closed',
    position: 100,
    playbackRate: 1,
    duration: 649,
    paused: false,
    ended: false,
    heldByUs: false,
    userPaused: false,
    playRequested: false,
    serverUrl: SERVER,
    ...fields,
  };
}

const decide = (fields) => decideGate(input(fields));

/** Decide with `seconds` of media covered ahead of the playhead at 100 s. */
const withAhead = (seconds, fields = {}) =>
  decide({ ...fields, view: view({ coverage: [[90, 100 + seconds]], ...fields.viewFields }) });

const page = (fields) => Object.freeze({ ...WATCHING, ...fields });

describe('constants', () => {
  it('holds under 8 s', () => {
    assert.equal(HOLD_SECONDS, 8);
  });

  it('resumes at 30 s', () => {
    assert.equal(RESUME_SECONDS, 30);
  });

  it('treats half a second from the end as the end', () => {
    assert.equal(END_TOLERANCE, 0.5);
  });
});

describe('effectiveRate', () => {
  it('keeps 1', () => assert.equal(effectiveRate(1), 1));
  it('keeps 2', () => assert.equal(effectiveRate(2), 2));
  it('keeps 0.25', () => assert.equal(effectiveRate(0.25), 0.25));
  it('makes 0 into 1', () => assert.equal(effectiveRate(0), 1));
  it('makes a negative rate into 1', () => assert.equal(effectiveRate(-1), 1));
  it('makes NaN into 1', () => assert.equal(effectiveRate(NaN), 1));
  it('makes Infinity into 1', () => assert.equal(effectiveRate(Infinity), 1));
  it('makes undefined into 1', () => assert.equal(effectiveRate(undefined), 1));
  it('makes a string into 1', () => assert.equal(effectiveRate('2'), 1));
});

describe('decideGate: decision shape', () => {
  it('has exactly action, reason, heldByUs and resume', () => {
    assert.deepEqual(Object.keys(decide()).sort(), ['action', 'heldByUs', 'reason', 'resume']);
  });

  it('is frozen', () => {
    assert.ok(Object.isFrozen(decide()));
  });

  it('says heldByUs after a hold', () => {
    assert.equal(withAhead(0).heldByUs, true);
  });

  it('says not heldByUs after a release', () => {
    assert.equal(withAhead(30, { heldByUs: true }).heldByUs, false);
  });

  it('says not heldByUs after none', () => {
    assert.equal(withAhead(30).heldByUs, false);
  });

  it('never resumes on a hold', () => {
    assert.equal(withAhead(0, { heldByUs: true }).resume, false);
  });

  it('never resumes on none', () => {
    assert.equal(withAhead(30).resume, false);
  });
});

for (const [rate, hold, resume] of [
  [1, 8, 30],
  [2, 16, 60],
  [0.5, 4, 15],
]) {
  describe(`decideGate: thresholds at ${rate}x (hold under ${hold} s, resume at ${resume} s of media)`, () => {
    const at = (seconds, fields = {}) => withAhead(seconds, { playbackRate: rate, ...fields });

    it('holds with nothing covered ahead', () => {
      assert.equal(at(0).action, 'hold');
    });

    it('holds just under the hold threshold', () => {
      assert.equal(at(hold - 0.1).action, 'hold');
    });

    it('does not hold exactly at the hold threshold', () => {
      assert.equal(at(hold).action, 'none');
    });

    it('does not hold just over the hold threshold', () => {
      assert.equal(at(hold + 0.1).action, 'none');
    });

    it('does not start a hold between the thresholds', () => {
      assert.equal(at((hold + resume) / 2).action, 'none');
    });

    it('keeps its hold between the thresholds', () => {
      assert.equal(at((hold + resume) / 2, { heldByUs: true }).action, 'hold');
    });

    it('keeps its hold just under the resume threshold', () => {
      assert.equal(at(resume - 0.1, { heldByUs: true }).action, 'hold');
    });

    it('releases its hold exactly at the resume threshold', () => {
      assert.equal(at(resume, { heldByUs: true }).action, 'release');
    });

    it('releases its hold over the resume threshold', () => {
      assert.equal(at(resume + 10, { heldByUs: true }).action, 'release');
    });

    it('releases with reason covered', () => {
      assert.equal(at(resume, { heldByUs: true }).reason, 'covered');
    });

    it('leaves a well-covered video alone with reason covered', () => {
      assert.equal(at(resume).reason, 'covered');
    });

    it('holds with reason preparing', () => {
      assert.equal(at(0).reason, 'preparing');
    });
  });
}

describe('decideGate: rate scaling', () => {
  it('holds at 2x with 10 s ahead, which is enough at 1x', () => {
    assert.equal(withAhead(10, { playbackRate: 2 }).action, 'hold');
  });

  it('does not hold at 0.5x with 5 s ahead, which is not enough at 1x', () => {
    assert.equal(withAhead(5, { playbackRate: 0.5 }).action, 'none');
  });

  it('holds at 1x with 5 s ahead', () => {
    assert.equal(withAhead(5).action, 'hold');
  });

  it('keeps a hold at 2x with 40 s ahead, which would release at 1x', () => {
    assert.equal(withAhead(40, { playbackRate: 2, heldByUs: true }).action, 'hold');
  });

  it('releases at 1x with 40 s ahead', () => {
    assert.equal(withAhead(40, { heldByUs: true }).action, 'release');
  });

  it('holds at 16x (the thresholds keep scaling)', () => {
    assert.equal(withAhead(100, { playbackRate: 16 }).action, 'hold');
  });

  it('treats a zero rate as 1x', () => {
    assert.equal(withAhead(8, { playbackRate: 0 }).action, 'none');
  });

  it('treats a missing rate as 1x', () => {
    assert.equal(withAhead(7, { playbackRate: undefined }).action, 'hold');
  });

  it('treats a NaN rate as 1x', () => {
    assert.equal(withAhead(8, { playbackRate: NaN }).action, 'none');
  });
});

describe('decideGate: coverage around the playhead', () => {
  it('holds when the playhead is in a gap, however much is covered later', () => {
    assert.equal(decide({ view: view({ coverage: [[0, 90], [110, 600]] }) }).action, 'hold');
  });

  it('holds when coverage starts just after the playhead', () => {
    assert.equal(decide({ view: view({ coverage: [[100.5, 600]] }) }).action, 'hold');
  });

  it('does not hold when the playhead sits exactly on the start of a long run', () => {
    assert.equal(decide({ view: view({ coverage: [[100, 600]] }) }).action, 'none');
  });

  it('holds exactly at the end of a run', () => {
    assert.equal(decide({ view: view({ coverage: [[0, 100]] }) }).action, 'hold');
  });

  it('counts touching windows as one run', () => {
    assert.equal(decide({ view: view({ coverage: [[90, 104], [104, 140]] }) }).action, 'none');
  });

  it('copes with unsorted coverage', () => {
    assert.equal(decide({ view: view({ coverage: [[104, 140], [90, 104]] }) }).action, 'none');
  });

  it('holds at the start of a video with nothing covered', () => {
    assert.equal(decide({ position: 0 }).action, 'hold');
  });

  it('plays from the start once the first two windows are in', () => {
    assert.equal(decide({ position: 0, heldByUs: true, view: view({ coverage: [[0, 46]] }) }).action, 'release');
  });

  it('keeps holding with only the first window (24 s) in', () => {
    assert.equal(decide({ position: 0, heldByUs: true, view: view({ coverage: [[0, 24]] }) }).action, 'hold');
  });

  it('holds after a seek into uncovered time', () => {
    assert.equal(decide({ position: 400, view: view({ coverage: [[0, 200]] }) }).action, 'hold');
  });
});

describe('decideGate: the end of the video', () => {
  it('releases a complete session even with coverage short of the duration', () => {
    const complete = view({ state: 'complete', coverage: [[0, 646.3]], duration: 649.2, fromCache: true });
    assert.equal(decide({ view: complete, heldByUs: true, duration: 649.2 }).action, 'release');
  });

  it('releases a complete session with reason complete', () => {
    assert.equal(decide({ view: view({ state: 'complete', coverage: [[0, 646.3]] }), heldByUs: true }).reason, 'complete');
  });

  it('does not hold a complete session past its last covered word', () => {
    const complete = view({ state: 'complete', coverage: [[0, 646.3]], duration: 649.2 });
    assert.equal(decide({ view: complete, position: 647, duration: 649.2 }).action, 'none');
  });

  it('does not hold a complete session with no coverage at all (a silent video)', () => {
    assert.equal(decide({ view: view({ state: 'complete', coverage: [] }) }).action, 'none');
  });

  it('does not hold a complete session with an unknown duration', () => {
    assert.equal(decide({ view: view({ state: 'complete', coverage: [[0, 10]], duration: null }), duration: NaN }).action, 'none');
  });

  it('does not hold a complete session at 16x', () => {
    assert.equal(decide({ view: view({ state: 'complete', coverage: [[0, 101]] }), playbackRate: 16 }).action, 'none');
  });

  it('releases when the covered run reaches the duration', () => {
    const v = view({ coverage: [[90, 120]] });
    assert.equal(decide({ view: v, duration: 120, heldByUs: true }).action, 'release');
  });

  it('releases with reason covered-to-end', () => {
    const v = view({ coverage: [[90, 120]] });
    assert.equal(decide({ view: v, duration: 120, heldByUs: true }).reason, 'covered-to-end');
  });

  it('releases when the run ends within half a second of the duration', () => {
    const v = view({ coverage: [[90, 119.5]] });
    assert.equal(decide({ view: v, duration: 120, heldByUs: true }).action, 'release');
  });

  it('keeps holding when the run ends just over half a second from the duration', () => {
    const v = view({ coverage: [[90, 119.4]] });
    assert.equal(decide({ view: v, duration: 120, heldByUs: true }).action, 'hold');
  });

  it('does not hold near the end with less than 8 s left, all covered', () => {
    const v = view({ coverage: [[90, 105]] });
    assert.equal(decide({ view: v, duration: 105, position: 102 }).action, 'none');
  });

  it('holds near the end when the rest is not covered', () => {
    const v = view({ coverage: [[90, 102.5]] });
    assert.equal(decide({ view: v, duration: 105, position: 102 }).action, 'hold');
  });

  it('uses the view\'s duration when the element\'s is NaN', () => {
    const v = view({ coverage: [[90, 120]], duration: 120 });
    assert.equal(decide({ view: v, duration: NaN, heldByUs: true }).action, 'release');
  });

  it('uses the view\'s duration when the element\'s is Infinity', () => {
    const v = view({ coverage: [[90, 120]], duration: 120 });
    assert.equal(decide({ view: v, duration: Infinity, heldByUs: true }).action, 'release');
  });

  it('prefers the element\'s duration to the view\'s', () => {
    const v = view({ coverage: [[90, 120]], duration: 120 });
    assert.equal(decide({ view: v, duration: 300, heldByUs: true }).action, 'hold');
  });

  it('never needs a duration: with none known, coverage alone decides', () => {
    const v = view({ coverage: [[90, 130]], duration: null });
    assert.equal(decide({ view: v, duration: null, heldByUs: true }).action, 'release');
  });

  it('with no duration known, a short run at the end still holds', () => {
    const v = view({ coverage: [[90, 105]], duration: null });
    assert.equal(decide({ view: v, duration: null }).action, 'hold');
  });

  it('does not count a run that ends near the duration but does not contain the playhead', () => {
    const v = view({ coverage: [[110, 649]] });
    assert.equal(decide({ view: v }).action, 'hold');
  });
});

describe('decideGate: the user\'s pause', () => {
  it('releases a hold without resuming when the user paused', () => {
    assert.equal(withAhead(30, { heldByUs: true, paused: true, userPaused: true }).resume, false);
  });

  it('still releases (drops the hold) when the user paused', () => {
    assert.equal(withAhead(30, { heldByUs: true, paused: true, userPaused: true }).action, 'release');
  });

  it('resumes a hold of its own when the user did not pause', () => {
    assert.equal(withAhead(30, { heldByUs: true, paused: true }).resume, true);
  });

  it('holds a video the user paused when coverage is short (so a play press is caught)', () => {
    assert.equal(withAhead(0, { paused: true }).action, 'hold');
  });

  it('keeps reason preparing for a user-paused hold', () => {
    assert.equal(withAhead(0, { heldByUs: true, paused: true, userPaused: true }).reason, 'preparing');
  });

  it('does not resume on unfiltered when the user paused', () => {
    assert.equal(decide({ unfiltered: true, heldByUs: true, paused: true, userPaused: true }).resume, false);
  });

  it('does not resume on complete when the user paused', () => {
    assert.equal(decide({ view: view({ state: 'complete' }), heldByUs: true, paused: true, userPaused: true }).resume, false);
  });
});

describe('decideGate: a play press during a hold', () => {
  it('holds again when the video is found playing under our hold', () => {
    assert.equal(withAhead(0, { heldByUs: true, paused: false }).action, 'hold');
  });

  it('says still-preparing when the video is found playing under our hold', () => {
    assert.equal(withAhead(0, { heldByUs: true, paused: false }).reason, 'still-preparing');
  });

  it('says still-preparing when the controller caught a play press', () => {
    assert.equal(withAhead(0, { heldByUs: true, paused: true, playRequested: true }).reason, 'still-preparing');
  });

  it('says still-preparing for a play press before the first view', () => {
    assert.equal(decide({ view: null, heldByUs: true, paused: false }).reason, 'still-preparing');
  });

  it('says still-preparing for a play press while not keeping up', () => {
    const v = view({ coverage: [[90, 102]], keepingUp: false });
    assert.equal(decide({ view: v, heldByUs: true, paused: false }).reason, 'still-preparing');
  });

  it('says preparing for a hold that begins on a playing video', () => {
    assert.equal(withAhead(0, { heldByUs: false, paused: false }).reason, 'preparing');
  });

  it('says preparing for a hold with no play press', () => {
    assert.equal(withAhead(0, { heldByUs: true, paused: true }).reason, 'preparing');
  });

  it('keeps a failure\'s reason on a play press', () => {
    assert.equal(decide({ view: view({ state: 'failed' }), heldByUs: true, paused: false }).reason, 'failed');
  });

  it('keeps an error\'s reason on a play press', () => {
    assert.equal(decide({ error: { kind: 'unreachable' }, heldByUs: true, paused: false }).reason, 'error');
  });

  it('releases on a play press when coverage has arrived meanwhile', () => {
    assert.equal(withAhead(30, { heldByUs: true, paused: false }).action, 'release');
  });
});

describe('decideGate: standing aside', () => {
  it('does nothing during an ad', () => {
    assert.equal(withAhead(0, { page: page({ adShowing: true }) }).action, 'none');
  });

  it('gives reason ad during an ad', () => {
    assert.equal(withAhead(0, { page: page({ adShowing: true }) }).reason, 'ad');
  });

  it('drops its hold (none, not release) when an ad starts', () => {
    assert.equal(withAhead(0, { page: page({ adShowing: true }), heldByUs: true }).action, 'none');
  });

  it('stands aside for an ad even when the server is unreachable', () => {
    assert.equal(decide({ page: page({ adShowing: true }), error: { kind: 'unreachable' } }).action, 'none');
  });

  it('holds again once the ad is over', () => {
    assert.equal(withAhead(0, { page: page({ adShowing: false }) }).action, 'hold');
  });

  it('does nothing while leaving', () => {
    assert.equal(withAhead(0, { page: page({ phase: 'leaving', videoId: null }) }).action, 'none');
  });

  it('gives reason leaving while leaving', () => {
    assert.equal(withAhead(0, { page: page({ phase: 'leaving', videoId: null }) }).reason, 'leaving');
  });

  it('drops its hold without playing when a navigation starts', () => {
    const d = withAhead(0, { page: page({ phase: 'leaving', videoId: null }), heldByUs: true });
    assert.deepEqual([d.action, d.resume], ['none', false]);
  });

  it('prefers leaving to ad', () => {
    assert.equal(decide({ page: page({ phase: 'leaving', videoId: null, adShowing: true }) }).reason, 'leaving');
  });

  it('does nothing on a non-watch page', () => {
    assert.equal(decide({ page: page({ phase: 'idle', videoId: null }) }).action, 'none');
  });

  it('gives reason not-watching on a non-watch page', () => {
    assert.equal(decide({ page: page({ phase: 'idle', videoId: null }) }).reason, 'not-watching');
  });

  it('does nothing without a video element', () => {
    assert.equal(decide({ page: page({ hasVideoElement: false }) }).reason, 'not-watching');
  });

  it('does nothing with no page state', () => {
    assert.equal(decide({ page: undefined }).action, 'none');
  });

  it('does nothing once the video has ended', () => {
    assert.equal(withAhead(0, { ended: true }).action, 'none');
  });

  it('gives reason ended once the video has ended', () => {
    assert.equal(withAhead(0, { ended: true }).reason, 'ended');
  });

  it('drops its hold without playing at the end', () => {
    const d = withAhead(0, { ended: true, heldByUs: true });
    assert.deepEqual([d.action, d.resume], ['none', false]);
  });

  it('does nothing at the end even with an error', () => {
    assert.equal(decide({ ended: true, error: { kind: 'unreachable' } }).action, 'none');
  });
});

describe('decideGate: Watch unfiltered', () => {
  it('releases its hold', () => {
    assert.equal(withAhead(0, { unfiltered: true, heldByUs: true }).action, 'release');
  });

  it('resumes the video it held', () => {
    assert.equal(withAhead(0, { unfiltered: true, heldByUs: true }).resume, true);
  });

  it('gives reason unfiltered', () => {
    assert.equal(withAhead(0, { unfiltered: true, heldByUs: true }).reason, 'unfiltered');
  });

  it('does nothing when not holding', () => {
    assert.equal(withAhead(0, { unfiltered: true }).action, 'none');
  });

  it('overrides a failure', () => {
    assert.equal(decide({ unfiltered: true, view: view({ state: 'failed' }) }).action, 'none');
  });

  it('overrides an unreachable server', () => {
    assert.equal(decide({ unfiltered: true, error: { kind: 'unreachable' } }).action, 'none');
  });

  it('overrides an audio error', () => {
    assert.equal(decide({ unfiltered: true, error: { kind: 'audio' } }).action, 'none');
  });

  it('overrides a missing view', () => {
    assert.equal(decide({ unfiltered: true, view: null }).action, 'none');
  });

  it('overrides a play press', () => {
    assert.equal(decide({ unfiltered: true, heldByUs: true, paused: false }).action, 'release');
  });

  it('still stands aside for an ad', () => {
    assert.equal(decide({ unfiltered: true, page: page({ adShowing: true }) }).reason, 'ad');
  });
});

describe('decideGate: before the first answer', () => {
  it('holds with no view', () => {
    assert.equal(decide({ view: null }).action, 'hold');
  });

  it('gives reason starting with no view', () => {
    assert.equal(decide({ view: null }).reason, 'starting');
  });

  it('holds with an undefined view', () => {
    assert.equal(decide({ view: undefined }).action, 'hold');
  });

  it('treats a view of another video as no view', () => {
    assert.equal(decide({ view: view({ videoId: OTHER, state: 'complete' }) }).reason, 'starting');
  });
});

describe('decideGate: session states', () => {
  for (const state of ['queued', 'fetching', 'preparing', 'transcribing', 'cancelled']) {
    it(`holds a ${state} session with nothing covered`, () => {
      assert.equal(decide({ view: view({ state }) }).reason, 'preparing');
    });

    it(`does not hold a ${state} session with enough covered`, () => {
      assert.equal(withAhead(30, { viewFields: { state } }).action, 'none');
    });
  }

  it('says not-keeping-up when the session is behind', () => {
    assert.equal(decide({ view: view({ keepingUp: false }) }).reason, 'not-keeping-up');
  });

  it('does not hold a session that is behind while enough is covered', () => {
    assert.equal(withAhead(30, { viewFields: { keepingUp: false } }).action, 'none');
  });
});

describe('decideGate: failures, fail-closed', () => {
  it('holds a failed session', () => {
    assert.equal(decide({ view: view({ state: 'failed', failureKind: 'network' }) }).action, 'hold');
  });

  it('gives reason failed', () => {
    assert.equal(decide({ view: view({ state: 'failed' }) }).reason, 'failed');
  });

  it('holds an unsupported session', () => {
    assert.equal(decide({ view: view({ state: 'unsupported' }) }).reason, 'unsupported');
  });

  it('lets the covered part of a session that failed part-way play', () => {
    assert.equal(withAhead(100, { viewFields: { state: 'failed' } }).action, 'none');
  });

  it('holds where the covered part of a failed session runs out', () => {
    assert.equal(withAhead(3, { viewFields: { state: 'failed' } }).reason, 'failed');
  });

  it('keeps holding a failed session held by us', () => {
    assert.equal(decide({ view: view({ state: 'failed' }), heldByUs: true, paused: true }).action, 'hold');
  });

  for (const kind of ['unreachable', 'timeout', 'http', 'bad_response', 'extension', 'audio']) {
    it(`holds on a ${kind} error`, () => {
      assert.equal(withAhead(100, { error: { kind } }).action, 'hold');
    });

    it(`gives reason error on a ${kind} error`, () => {
      assert.equal(withAhead(100, { error: { kind } }).reason, 'error');
    });
  }

  it('holds on an error before the first view', () => {
    assert.equal(decide({ view: null, error: { kind: 'unreachable' } }).reason, 'error');
  });

  it('prefers the error to a failed view', () => {
    assert.equal(decide({ view: view({ state: 'failed' }), error: { kind: 'unreachable' } }).reason, 'error');
  });

  for (const kind of ['unreachable', 'timeout', 'http', 'bad_response', 'extension']) {
    it(`ignores a ${kind} error once the session is complete`, () => {
      assert.equal(decide({ view: view({ state: 'complete' }), error: { kind } }).action, 'none');
    });
  }

  it('holds on an audio error even when complete', () => {
    assert.equal(decide({ view: view({ state: 'complete' }), error: { kind: 'audio' } }).reason, 'error');
  });

  it('treats a missing fail policy as closed', () => {
    assert.equal(decide({ failPolicy: undefined, error: { kind: 'unreachable' } }).action, 'hold');
  });
});

describe('decideGate: failures, fail-open', () => {
  it('lets a failed session play', () => {
    assert.equal(decide({ failPolicy: 'open', view: view({ state: 'failed' }) }).action, 'none');
  });

  it('gives reason fail-open', () => {
    assert.equal(decide({ failPolicy: 'open', view: view({ state: 'failed' }) }).reason, 'fail-open');
  });

  it('releases a hold on an error', () => {
    assert.equal(decide({ failPolicy: 'open', error: { kind: 'unreachable' }, heldByUs: true }).action, 'release');
  });

  it('lets an unsupported video play', () => {
    assert.equal(decide({ failPolicy: 'open', view: view({ state: 'unsupported' }) }).action, 'none');
  });

  it('still holds for coverage (fail-open is about failures only)', () => {
    assert.equal(decide({ failPolicy: 'open' }).action, 'hold');
  });

  it('still holds before the first view', () => {
    assert.equal(decide({ failPolicy: 'open', view: null }).action, 'hold');
  });
});

describe('mediaAhead', () => {
  it('is Infinity for a complete session', () => {
    assert.equal(mediaAhead(view({ state: 'complete' }), input()), Infinity);
  });

  it('is 0 in a gap', () => {
    assert.equal(mediaAhead(view({ coverage: [[0, 50]] }), input()), 0);
  });

  it('is the run ahead', () => {
    assert.equal(mediaAhead(view({ coverage: [[0, 120]] }), input()), 20);
  });

  it('is Infinity when the run reaches the end', () => {
    assert.equal(mediaAhead(view({ coverage: [[0, 649]] }), input()), Infinity);
  });
});

describe('gateOverlay: hidden', () => {
  it('is hidden when not holding', () => {
    assert.equal(gateOverlay(input({ view: view({ state: 'complete' }) })).visible, false);
  });

  it('is the shared hidden model when not holding', () => {
    assert.equal(gateOverlay(input({ view: view({ state: 'complete' }) })), HIDDEN_OVERLAY);
  });

  it('hides the button when hidden', () => {
    assert.equal(HIDDEN_OVERLAY.showUnfiltered, false);
  });

  it('is hidden on release', () => {
    assert.equal(gateOverlay(input({ unfiltered: true, heldByUs: true })).visible, false);
  });

  it('is hidden during an ad', () => {
    assert.equal(gateOverlay(input({ page: page({ adShowing: true }) })).visible, false);
  });

  it('is hidden fail-open', () => {
    assert.equal(gateOverlay(input({ failPolicy: 'open', error: { kind: 'unreachable' } })).visible, false);
  });

  it('has exactly the documented fields', () => {
    assert.deepEqual(Object.keys(HIDDEN_OVERLAY).sort(), ['detail', 'percent', 'showUnfiltered', 'title', 'visible']);
  });

  it('uses the decision it is given', () => {
    assert.equal(gateOverlay(input(), { action: 'none', reason: 'covered', heldByUs: false, resume: false }).visible, false);
  });
});

describe('gateOverlay: shown', () => {
  const shown = (fields) => gateOverlay(input(fields));

  it('is visible on a hold', () => {
    assert.equal(shown({}).visible, true);
  });

  it('offers Watch unfiltered on a hold', () => {
    assert.equal(shown({}).showUnfiltered, true);
  });

  it('offers Watch unfiltered on a failure', () => {
    assert.equal(shown({ view: view({ state: 'failed' }) }).showUnfiltered, true);
  });

  it('offers Watch unfiltered on an error', () => {
    assert.equal(shown({ error: { kind: 'unreachable' } }).showUnfiltered, true);
  });

  it('is frozen', () => {
    assert.ok(Object.isFrozen(shown({})));
  });

  it('says it is starting before the first view', () => {
    assert.equal(shown({ view: null }).title, 'FoulFilter is starting');
  });

  it('has no percent before the first view', () => {
    assert.equal(shown({ view: null }).percent, null);
  });

  it('says it is preparing', () => {
    assert.equal(shown({}).title, 'FoulFilter is preparing this video');
  });

  for (const [state, text] of [
    ['queued', /Waiting/],
    ['fetching', /Fetching the audio/],
    ['preparing', /Preparing the audio/],
    ['transcribing', /Listening ahead/],
    ['cancelled', /Starting again/],
  ]) {
    it(`describes the ${state} state`, () => {
      assert.match(shown({ view: view({ state }) }).detail, text);
    });
  }

  it('says playback starts by itself', () => {
    assert.match(shown({}).detail, /Playback starts when enough is ready/);
  });

  it('shows 0% with nothing covered while transcribing', () => {
    assert.equal(shown({}).percent, 0);
  });

  it('shows the way to the resume threshold while transcribing', () => {
    assert.equal(shown({ view: view({ coverage: [[90, 106]] }) }).percent, 20);
  });

  it('shows the way to the resume threshold scaled by rate', () => {
    assert.equal(shown({ playbackRate: 2, view: view({ coverage: [[90, 112]] }) }).percent, 20);
  });

  it('rounds the percent down', () => {
    assert.equal(shown({ view: view({ coverage: [[90, 102.99]] }) }).percent, 9);
  });

  it('never shows 100% while holding', () => {
    assert.equal(shown({ heldByUs: true, paused: true, view: view({ coverage: [[90, 129.99]] }) }).percent, 99);
  });

  it('has no percent while fetching', () => {
    assert.equal(shown({ view: view({ state: 'fetching' }) }).percent, null);
  });

  it('has no percent while queued', () => {
    assert.equal(shown({ view: view({ state: 'queued' }) }).percent, null);
  });

  it('tells the user a paused video stays paused', () => {
    assert.match(shown({ heldByUs: true, paused: true, userPaused: true }).detail, /stay paused until you press play/);
  });

  it('does not mention pausing when the user did not pause', () => {
    assert.doesNotMatch(shown({ heldByUs: true, paused: true }).detail, /You paused/);
  });

  it('says not yet on a play press', () => {
    assert.match(shown({ heldByUs: true, paused: false }).title, /^Not yet/);
  });

  it('keeps the percent on a play press', () => {
    assert.equal(shown({ heldByUs: true, paused: false, view: view({ coverage: [[90, 106]] }) }).percent, 20);
  });

  it('says it is not keeping up', () => {
    assert.equal(shown({ view: view({ keepingUp: false }) }).title, 'FoulFilter is not keeping up');
  });

  it('explains not keeping up', () => {
    assert.match(shown({ view: view({ keepingUp: false }) }).detail, /more slowly than the video plays/);
  });
});

describe('gateOverlay: failures', () => {
  const failed = (failureKind, reason = null) =>
    gateOverlay(input({ view: view({ state: 'failed', failureKind, reason }) }));

  it('says it could not prepare the video', () => {
    assert.equal(failed('network').title, 'FoulFilter could not prepare this video');
  });

  it('has no percent', () => {
    assert.equal(failed('network').percent, null);
  });

  for (const [kind, text] of [
    ['not_installed', /yt-dlp is not installed/],
    ['js_runtime_missing', /needs Deno/],
    ['unavailable', /not available/],
    ['sign_in_required', /signed-in viewer/],
    ['unsupported', /cannot fetch this kind of video/],
    ['network', /online/],
    ['failed', /Updating yt-dlp/],
  ]) {
    it(`advises on ${kind}`, () => {
      assert.match(failed(kind).detail, text);
    });
  }

  it('adds the server\'s reason to the advice', () => {
    assert.equal(
      failed('unavailable', 'Video unavailable. This video is private').detail,
      'YouTube says this video is not available. (Video unavailable. This video is private)',
    );
  });

  it('does not add a blank reason', () => {
    assert.equal(failed('unavailable', '  ').detail, 'YouTube says this video is not available.');
  });

  it('uses the generic advice for an unknown failure kind', () => {
    assert.match(failed('brand_new_kind').detail, /Updating yt-dlp/);
  });

  it('gives the server\'s reason alone when there is no failure kind', () => {
    assert.equal(failed(null, 'The engine crashed.').detail, 'The engine crashed.');
  });

  it('gives generic advice with neither kind nor reason', () => {
    assert.match(failed(null).detail, /Updating yt-dlp/);
  });
});

describe('gateOverlay: unsupported', () => {
  const unsupported = (reason) => gateOverlay(input({ view: view({ state: 'unsupported', reason }) }));

  it('says it cannot filter this video', () => {
    assert.equal(unsupported(null).title, 'FoulFilter cannot filter this video');
  });

  it('gives the server\'s reason', () => {
    assert.equal(unsupported('This live event will begin in 3 hours.').detail, 'This live event will begin in 3 hours.');
  });

  it('explains livestreams without a reason', () => {
    assert.match(unsupported(null).detail, /Livestreams/);
  });

  it('explains livestreams with a blank reason', () => {
    assert.match(unsupported('   ').detail, /Livestreams/);
  });
});

describe('gateOverlay: errors', () => {
  const errored = (error, fields = {}) => gateOverlay(input({ error, ...fields }));

  it('says the server is not reachable', () => {
    assert.equal(errored({ kind: 'unreachable' }).title, 'FoulFilterNet is not reachable');
  });

  it('asks whether the service is running', () => {
    assert.match(errored({ kind: 'unreachable' }).detail, /Is the service running\?/);
  });

  it('names the server URL', () => {
    assert.match(errored({ kind: 'unreachable' }, { serverUrl: 'http://127.0.0.1:9000' }).detail, /127\.0\.0\.1:9000/);
  });

  it('copes with no server URL', () => {
    assert.match(errored({ kind: 'unreachable' }, { serverUrl: undefined }).detail, /Could not reach/);
  });

  it('says the server is not answering on timeout', () => {
    assert.equal(errored({ kind: 'timeout' }).title, 'FoulFilterNet is not answering');
  });

  it('gives the http status and detail', () => {
    assert.match(errored({ kind: 'http', status: 503, detail: 'Stopping.' }).detail, /503: Stopping\./);
  });

  it('gives a generic title for an http error', () => {
    assert.equal(errored({ kind: 'http', status: 500 }).title, 'FoulFilterNet had a problem');
  });

  it('suggests reloading on an extension error', () => {
    assert.match(errored({ kind: 'extension' }).detail, /Reload/);
  });

  it('says it lost the extension on an extension error', () => {
    assert.equal(errored({ kind: 'extension' }).title, 'FoulFilter lost touch with the extension');
  });

  it('says the sound cannot be filtered on an audio error', () => {
    assert.equal(errored({ kind: 'audio' }).title, 'FoulFilter cannot filter the sound on this page');
  });

  it('asks for a page reload on an audio error', () => {
    assert.match(errored({ kind: 'audio' }).detail, /Reload the page/);
  });

  it('has no percent on an error', () => {
    assert.equal(errored({ kind: 'unreachable' }).percent, null);
  });
});
