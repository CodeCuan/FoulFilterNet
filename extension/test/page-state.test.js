import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  createPageState,
  INITIAL_PAGE_STATE,
  isFilterable,
  PHASES,
  reducePageState,
  shouldMute,
} from '../src/page-state.js';

const A = 'dQw4w9WgXcQ';
const B = 'jNQXAC9IVRw';
const url = (id, extra = '') => `https://www.youtube.com/watch?v=${id}${extra}`;
const HOME = 'https://www.youtube.com/';
const SEARCH = 'https://www.youtube.com/results?search_query=x';

const start = { type: 'navigate-start' };
const finish = (u) => ({ type: 'navigate-finish', url: u });
const initial = (u) => ({ type: 'initial', url: u });
const element = (present) => ({ type: 'video-element', present });
const ad = (showing) => ({ type: 'ad', showing });
const pagehide = { type: 'pagehide' };

/** The state after a sequence of events from the initial state. */
const run = (...events) => events.reduce(reducePageState, INITIAL_PAGE_STATE);

/** A state with the given fields, the rest from the initial state. */
const state = (fields) => Object.freeze({ ...INITIAL_PAGE_STATE, ...fields });

describe('INITIAL_PAGE_STATE', () => {
  it('is idle', () => {
    assert.equal(INITIAL_PAGE_STATE.phase, 'idle');
  });

  it('has no video', () => {
    assert.equal(INITIAL_PAGE_STATE.videoId, null);
  });

  it('shows no ad', () => {
    assert.equal(INITIAL_PAGE_STATE.adShowing, false);
  });

  it('has not found the element', () => {
    assert.equal(INITIAL_PAGE_STATE.hasVideoElement, false);
  });

  it('is generation 0', () => {
    assert.equal(INITIAL_PAGE_STATE.generation, 0);
  });

  it('is frozen', () => {
    assert.ok(Object.isFrozen(INITIAL_PAGE_STATE));
  });

  it('has exactly the documented fields', () => {
    assert.deepEqual(Object.keys(INITIAL_PAGE_STATE).sort(), [
      'adShowing',
      'generation',
      'hasVideoElement',
      'phase',
      'videoId',
    ]);
  });
});

describe('PHASES', () => {
  it('lists the three phases', () => {
    assert.deepEqual([...PHASES], ['idle', 'leaving', 'watching']);
  });
});

describe('initial', () => {
  it('on a watch page starts watching its video', () => {
    assert.deepEqual(run(initial(url(A))), state({ phase: 'watching', videoId: A, generation: 1 }));
  });

  it('on a watch page with a start time starts watching its video', () => {
    assert.equal(run(initial(url(A, '&t=90s'))).videoId, A);
  });

  it('on the home page stays idle', () => {
    assert.equal(run(initial(HOME)), INITIAL_PAGE_STATE);
  });

  it('on a short stays idle', () => {
    assert.equal(run(initial(`https://www.youtube.com/shorts/${A}`)).phase, 'idle');
  });

  it('on a watch page with a bad id stays idle', () => {
    assert.equal(run(initial(url('short'))).phase, 'idle');
  });

  it('for the video already watched changes nothing', () => {
    const watching = run(initial(url(A)));
    assert.equal(reducePageState(watching, initial(url(A))), watching);
  });

  it('for another video while watching moves to it', () => {
    assert.equal(run(initial(url(A)), initial(url(B))).videoId, B);
  });

  it('for another video while watching starts a new generation', () => {
    assert.equal(run(initial(url(A)), initial(url(B))).generation, 2);
  });

  it('on a non-watch page while watching goes idle', () => {
    assert.equal(run(initial(url(A)), initial(HOME)).phase, 'idle');
  });

  it('without a url changes nothing', () => {
    assert.equal(reducePageState(INITIAL_PAGE_STATE, { type: 'initial' }), INITIAL_PAGE_STATE);
  });

  it('with a non-string url changes nothing', () => {
    assert.equal(reducePageState(INITIAL_PAGE_STATE, { type: 'initial', url: new URL(url(A)) }), INITIAL_PAGE_STATE);
  });
});

describe('navigate-start', () => {
  it('while watching becomes leaving', () => {
    assert.equal(run(initial(url(A)), start).phase, 'leaving');
  });

  it('while watching drops the video id', () => {
    assert.equal(run(initial(url(A)), start).videoId, null);
  });

  it('while watching keeps the generation', () => {
    assert.equal(run(initial(url(A)), start).generation, 1);
  });

  it('while idle becomes leaving (the miniplayer may be playing)', () => {
    assert.equal(run(start).phase, 'leaving');
  });

  it('while leaving changes nothing', () => {
    const leaving = run(initial(url(A)), start);
    assert.equal(reducePageState(leaving, start), leaving);
  });

  it('keeps the element', () => {
    assert.equal(run(element(true), initial(url(A)), start).hasVideoElement, true);
  });

  it('keeps an ad showing', () => {
    assert.equal(run(initial(url(A)), ad(true), start).adShowing, true);
  });
});

describe('navigate-finish', () => {
  it('after leaving, on a watch page, starts watching the new video', () => {
    assert.deepEqual(
      run(initial(url(A)), start, finish(url(B))),
      state({ phase: 'watching', videoId: B, generation: 2 }),
    );
  });

  it('after leaving, on a non-watch page, goes idle', () => {
    assert.deepEqual(run(initial(url(A)), start, finish(HOME)), state({ phase: 'idle', generation: 1 }));
  });

  it('after leaving, on a watch page with a bad id, goes idle', () => {
    assert.equal(run(initial(url(A)), start, finish(url('nope'))).phase, 'idle');
  });

  it('after leaving, back on the same video, watches it again', () => {
    assert.equal(run(initial(url(A)), start, finish(url(A, '&t=30'))).videoId, A);
  });

  it('after leaving, back on the same video, starts a new generation (decided)', () => {
    assert.equal(run(initial(url(A)), start, finish(url(A, '&t=30'))).generation, 2);
  });

  it('from idle, on a watch page, starts watching', () => {
    assert.equal(run(initial(HOME), start, finish(url(A))).phase, 'watching');
  });

  it('from idle, on a watch page, is generation 1', () => {
    assert.equal(run(initial(HOME), start, finish(url(A))).generation, 1);
  });

  it('from idle with no navigate-start, on a watch page, starts watching', () => {
    assert.equal(run(finish(url(A))).videoId, A);
  });

  it('from idle, on a non-watch page, changes nothing', () => {
    assert.equal(reducePageState(INITIAL_PAGE_STATE, finish(SEARCH)), INITIAL_PAGE_STATE);
  });

  it('while watching, for the same video with no navigate-start, changes nothing', () => {
    const watching = run(initial(url(A)));
    assert.equal(reducePageState(watching, finish(url(A, '&t=5'))), watching);
  });

  it('while watching, for another video with no navigate-start, moves to it', () => {
    assert.equal(run(initial(url(A)), finish(url(B))).videoId, B);
  });

  it('while watching, for another video with no navigate-start, starts a new generation', () => {
    assert.equal(run(initial(url(A)), finish(url(B))).generation, 2);
  });

  it('while watching, on a non-watch page, goes idle', () => {
    assert.equal(run(initial(url(A)), finish(HOME)).phase, 'idle');
  });

  it('going idle keeps the generation', () => {
    assert.equal(run(initial(url(A)), finish(HOME)).generation, 1);
  });

  it('without a url changes nothing', () => {
    const leaving = run(initial(url(A)), start);
    assert.equal(reducePageState(leaving, { type: 'navigate-finish' }), leaving);
  });

  it('with a numeric url changes nothing', () => {
    const leaving = run(initial(url(A)), start);
    assert.equal(reducePageState(leaving, { type: 'navigate-finish', url: 42 }), leaving);
  });

  it('keeps the element', () => {
    assert.equal(run(element(true), initial(url(A)), start, finish(url(B))).hasVideoElement, true);
  });

  it('keeps the ad state', () => {
    assert.equal(run(initial(url(A)), start, ad(true), finish(url(B))).adShowing, true);
  });
});

describe('generation', () => {
  it('counts every watching period across a long session', () => {
    const s = run(
      initial(url(A)),
      start,
      finish(url(B)),
      start,
      finish(HOME),
      start,
      finish(url(A)),
      start,
      finish(url(A)),
    );
    assert.equal(s.generation, 4);
  });

  it('does not rise for leaving', () => {
    assert.equal(run(initial(url(A)), start, start).generation, 1);
  });

  it('does not rise for idle pages', () => {
    assert.equal(run(start, finish(HOME), start, finish(SEARCH)).generation, 0);
  });

  it('does not rise for element or ad events', () => {
    assert.equal(run(initial(url(A)), element(true), ad(true), ad(false), element(false)).generation, 1);
  });

  it('does not rise for pagehide', () => {
    assert.equal(run(initial(url(A)), pagehide).generation, 1);
  });

  it('rises again when a page comes back after pagehide', () => {
    assert.equal(run(initial(url(A)), pagehide, initial(url(A))).generation, 2);
  });
});

describe('video-element', () => {
  it('present marks the element found', () => {
    assert.equal(run(element(true)).hasVideoElement, true);
  });

  it('absent marks it gone', () => {
    assert.equal(run(element(true), element(false)).hasVideoElement, false);
  });

  it('present twice changes nothing the second time', () => {
    const found = run(element(true));
    assert.equal(reducePageState(found, element(true)), found);
  });

  it('absent while never found changes nothing', () => {
    assert.equal(reducePageState(INITIAL_PAGE_STATE, element(false)), INITIAL_PAGE_STATE);
  });

  it('appearing late while watching keeps watching the same video', () => {
    assert.deepEqual(
      run(initial(url(A)), element(true)),
      state({ phase: 'watching', videoId: A, generation: 1, hasVideoElement: true }),
    );
  });

  it('appearing while leaving stays leaving', () => {
    assert.equal(run(initial(url(A)), start, element(true)).phase, 'leaving');
  });

  it('appearing while idle stays idle', () => {
    assert.equal(run(element(true)).phase, 'idle');
  });

  it('with a non-boolean present changes nothing', () => {
    assert.equal(reducePageState(INITIAL_PAGE_STATE, { type: 'video-element', present: 1 }), INITIAL_PAGE_STATE);
  });

  it('without present changes nothing', () => {
    assert.equal(reducePageState(INITIAL_PAGE_STATE, { type: 'video-element' }), INITIAL_PAGE_STATE);
  });
});

describe('ad', () => {
  it('showing while watching is recorded', () => {
    assert.equal(run(initial(url(A)), ad(true)).adShowing, true);
  });

  it('showing while watching keeps the video', () => {
    assert.equal(run(initial(url(A)), ad(true)).videoId, A);
  });

  it('showing while watching keeps the phase', () => {
    assert.equal(run(initial(url(A)), ad(true)).phase, 'watching');
  });

  it('ending while watching is recorded', () => {
    assert.equal(run(initial(url(A)), ad(true), ad(false)).adShowing, false);
  });

  it('showing while leaving is recorded', () => {
    assert.equal(run(initial(url(A)), start, ad(true)).adShowing, true);
  });

  it('showing while leaving stays leaving', () => {
    assert.equal(run(initial(url(A)), start, ad(true)).phase, 'leaving');
  });

  it('ending while leaving is recorded', () => {
    assert.equal(run(initial(url(A)), ad(true), start, ad(false)).adShowing, false);
  });

  it('showing while idle is recorded', () => {
    assert.equal(run(ad(true)).adShowing, true);
  });

  it('showing twice changes nothing the second time', () => {
    const showing = run(initial(url(A)), ad(true));
    assert.equal(reducePageState(showing, ad(true)), showing);
  });

  it('ending with no ad showing changes nothing', () => {
    assert.equal(reducePageState(INITIAL_PAGE_STATE, ad(false)), INITIAL_PAGE_STATE);
  });

  it('with a string showing changes nothing', () => {
    assert.equal(reducePageState(INITIAL_PAGE_STATE, { type: 'ad', showing: 'true' }), INITIAL_PAGE_STATE);
  });

  it('a pre-roll that starts before navigate-finish is still showing when watching begins', () => {
    const s = run(initial(url(A)), start, ad(true), finish(url(B)));
    assert.deepEqual([s.phase, s.adShowing], ['watching', true]);
  });
});

describe('pagehide', () => {
  it('while watching goes idle', () => {
    assert.equal(run(initial(url(A)), pagehide).phase, 'idle');
  });

  it('while watching drops the video', () => {
    assert.equal(run(initial(url(A)), pagehide).videoId, null);
  });

  it('while leaving goes idle', () => {
    assert.equal(run(initial(url(A)), start, pagehide).phase, 'idle');
  });

  it('while idle changes nothing', () => {
    assert.equal(reducePageState(INITIAL_PAGE_STATE, pagehide), INITIAL_PAGE_STATE);
  });

  it('keeps the element', () => {
    assert.equal(run(element(true), initial(url(A)), pagehide).hasVideoElement, true);
  });

  it('keeps the ad state', () => {
    assert.equal(run(initial(url(A)), ad(true), pagehide).adShowing, true);
  });
});

describe('malformed events', () => {
  for (const [name, event] of [
    ['undefined', undefined],
    ['null', null],
    ['a string', 'navigate-start'],
    ['a number', 7],
    ['an empty object', {}],
    ['an unknown type', { type: 'navigate-middle' }],
    ['a type in the wrong case', { type: 'Navigate-Start' }],
    ['a DOM event name', { type: 'yt-navigate-start' }],
  ]) {
    it(`${name} changes nothing`, () => {
      const watching = run(initial(url(A)));
      assert.equal(reducePageState(watching, event), watching);
    });
  }
});

describe('reducePageState', () => {
  it('never mutates the state it is given', () => {
    const before = state({ phase: 'watching', videoId: A, generation: 3 });
    reducePageState(before, start);
    assert.deepEqual(before, state({ phase: 'watching', videoId: A, generation: 3 }));
  });

  it('returns a frozen state on change', () => {
    assert.ok(Object.isFrozen(run(initial(url(A)))));
  });

  it('returns a new object on change', () => {
    assert.notEqual(run(ad(true)), INITIAL_PAGE_STATE);
  });

  it('works on an unfrozen state too', () => {
    assert.equal(reducePageState({ ...INITIAL_PAGE_STATE }, initial(url(A))).videoId, A);
  });

  it('only ever has a video id while watching', () => {
    const events = [initial(url(A)), start, element(true), finish(url(B)), ad(true), start, finish(HOME), pagehide];
    let s = INITIAL_PAGE_STATE;
    for (const event of events) {
      s = reducePageState(s, event);
      assert.equal(s.videoId !== null, s.phase === 'watching', JSON.stringify(event));
    }
  });
});

describe('the ADR-0007 navigation sequence', () => {
  // navigate-start; location already shows B while the element plays A;
  // the element's source changes; navigate-finish; new audio.
  it('mutes from navigate-start until navigate-finish', () => {
    const watchingA = run(element(true), initial(url(A)));
    const leaving = reducePageState(watchingA, start);
    const watchingB = reducePageState(leaving, finish(url(B)));
    assert.deepEqual(
      [shouldMute(watchingA), shouldMute(leaving), shouldMute(watchingB)],
      [false, true, false],
    );
  });

  it('is filterable before and after, not during', () => {
    const watchingA = run(element(true), initial(url(A)));
    const leaving = reducePageState(watchingA, start);
    const watchingB = reducePageState(leaving, finish(url(B)));
    assert.deepEqual(
      [isFilterable(watchingA), isFilterable(leaving), isFilterable(watchingB)],
      [true, false, true],
    );
  });

  it('never names the new video before navigate-finish', () => {
    assert.equal(run(element(true), initial(url(A)), start).videoId, null);
  });
});

describe('shouldMute', () => {
  it('is false when idle', () => {
    assert.equal(shouldMute(state({})), false);
  });

  it('is true when leaving', () => {
    assert.equal(shouldMute(state({ phase: 'leaving' })), true);
  });

  it('is true when leaving during an ad', () => {
    assert.equal(shouldMute(state({ phase: 'leaving', adShowing: true })), true);
  });

  it('is true when leaving with no element', () => {
    assert.equal(shouldMute(state({ phase: 'leaving', hasVideoElement: false })), true);
  });

  it('is false when watching', () => {
    assert.equal(shouldMute(state({ phase: 'watching', videoId: A, hasVideoElement: true })), false);
  });

  it('is false when watching during an ad', () => {
    assert.equal(shouldMute(state({ phase: 'watching', videoId: A, adShowing: true })), false);
  });
});

describe('isFilterable', () => {
  const watching = { phase: 'watching', videoId: A, hasVideoElement: true, generation: 1 };

  it('is true when watching with the element and no ad', () => {
    assert.equal(isFilterable(state(watching)), true);
  });

  it('is false during an ad', () => {
    assert.equal(isFilterable(state({ ...watching, adShowing: true })), false);
  });

  it('is false without the element', () => {
    assert.equal(isFilterable(state({ ...watching, hasVideoElement: false })), false);
  });

  it('is false when leaving', () => {
    assert.equal(isFilterable(state({ ...watching, phase: 'leaving', videoId: null })), false);
  });

  it('is false when idle with the element', () => {
    assert.equal(isFilterable(state({ hasVideoElement: true })), false);
  });

  it('is false when idle', () => {
    assert.equal(isFilterable(INITIAL_PAGE_STATE), false);
  });

  it('becomes true when the element turns up late', () => {
    const before = run(initial(url(A)));
    assert.deepEqual([isFilterable(before), isFilterable(reducePageState(before, element(true)))], [false, true]);
  });

  it('becomes true again when an ad ends', () => {
    assert.equal(isFilterable(run(element(true), initial(url(A)), ad(true), ad(false))), true);
  });

  it('never holds together with shouldMute', () => {
    for (const phase of PHASES) {
      for (const adShowing of [false, true]) {
        for (const hasVideoElement of [false, true]) {
          const s = state({ phase, videoId: phase === 'watching' ? A : null, adShowing, hasVideoElement });
          assert.ok(!(isFilterable(s) && shouldMute(s)), JSON.stringify(s));
        }
      }
    }
  });
});

describe('createPageState', () => {
  it('starts at the initial state', () => {
    assert.equal(createPageState().state, INITIAL_PAGE_STATE);
  });

  it('can start at a given state', () => {
    const given = state({ adShowing: true });
    assert.equal(createPageState({ initial: given }).state, given);
  });

  it('applies an event', () => {
    const page = createPageState();
    page.dispatch(initial(url(A)));
    assert.equal(page.state.videoId, A);
  });

  it('dispatch reports a change', () => {
    assert.equal(createPageState().dispatch(initial(url(A))), true);
  });

  it('dispatch reports no change', () => {
    assert.equal(createPageState().dispatch(initial(HOME)), false);
  });

  it('calls onChange with the new state', () => {
    const calls = [];
    createPageState({ onChange: (...args) => calls.push(args) }).dispatch(initial(url(A)));
    assert.equal(calls[0][0].videoId, A);
  });

  it('calls onChange with the previous state', () => {
    const calls = [];
    createPageState({ onChange: (...args) => calls.push(args) }).dispatch(initial(url(A)));
    assert.equal(calls[0][1], INITIAL_PAGE_STATE);
  });

  it('calls onChange with the event', () => {
    const calls = [];
    const event = initial(url(A));
    createPageState({ onChange: (...args) => calls.push(args) }).dispatch(event);
    assert.equal(calls[0][2], event);
  });

  it('has updated its state by the time onChange runs', () => {
    let seen = null;
    const page = createPageState({ onChange: () => (seen = page.state.videoId) });
    page.dispatch(initial(url(A)));
    assert.equal(seen, A);
  });

  it('does not call onChange for a no-op event', () => {
    let calls = 0;
    const page = createPageState({ onChange: () => calls++ });
    page.dispatch(ad(false));
    page.dispatch(element(false));
    page.dispatch(pagehide);
    page.dispatch(finish(HOME));
    page.dispatch({ type: 'nonsense' });
    assert.equal(calls, 0);
  });

  it('does not call onChange for a repeated event', () => {
    let calls = 0;
    const page = createPageState({ onChange: () => calls++ });
    page.dispatch(start);
    page.dispatch(start);
    page.dispatch(start);
    assert.equal(calls, 1);
  });

  it('calls onChange once per real change', () => {
    const phases = [];
    const page = createPageState({ onChange: (s) => phases.push(s.phase) });
    for (const event of [initial(url(A)), start, finish(url(B)), finish(url(B)), start, finish(HOME), pagehide]) {
      page.dispatch(event);
    }
    assert.deepEqual(phases, ['watching', 'leaving', 'watching', 'leaving', 'idle']);
  });

  it('works without onChange', () => {
    const page = createPageState();
    assert.doesNotThrow(() => page.dispatch(initial(url(A))));
  });
});
