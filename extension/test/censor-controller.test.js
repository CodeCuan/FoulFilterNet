// censor-controller.js against a fake <video> (an EventTarget whose fields
// the test sets, then fires the event a browser would), a fake graph that
// records what it is told, and hand-run interval timers.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { MEDIA_EVENTS, TICK_MS, createCensorController } from '../src/censor-controller.js';

class FakeVideo extends EventTarget {
  currentTime = 10;
  playbackRate = 1;
  paused = false;
  seeking = false;
  readyState = 4;
  listeners = new Map();

  addEventListener(type, listener) {
    this.listeners.set(type, (this.listeners.get(type) ?? 0) + 1);
    super.addEventListener(type, listener);
  }
  removeEventListener(type, listener) {
    this.listeners.set(type, (this.listeners.get(type) ?? 0) - 1);
    super.removeEventListener(type, listener);
  }
  get listenerCount() {
    return [...this.listeners.values()].reduce((a, b) => a + b, 0);
  }
  fire(type) {
    this.dispatchEvent(new Event(type));
  }
}

class FakeGraph {
  currentTime = 100;
  calls = [];
  apply(plan, method) {
    this.calls.push(['apply', plan, method]);
  }
  muteNow() {
    this.calls.push(['mute']);
  }
  openNow() {
    this.calls.push(['open']);
  }
  get applies() {
    return this.calls.filter((c) => c[0] === 'apply');
  }
  get last() {
    return this.calls.at(-1);
  }
  get lastPlan() {
    return this.applies.at(-1)?.[1];
  }
}

class FakeTimers {
  active = new Map();
  nextId = 1;
  created = 0;
  cleared = 0;
  delays = [];
  setInterval = (fn, ms) => {
    const id = this.nextId++;
    this.active.set(id, fn);
    this.created++;
    this.delays.push(ms);
    return id;
  };
  clearInterval = (id) => {
    if (this.active.delete(id)) this.cleared++;
  };
  /** Run every active interval once. */
  tick() {
    for (const fn of [...this.active.values()]) fn();
  }
  get running() {
    return this.active.size;
  }
}

const hit = (start, end) => ({ start, end, phrase: 'x' });

/** A controller over a playing video (unless overridden) with one hit ahead. */
function setup({ video: videoFields = {}, ...options } = {}) {
  const video = Object.assign(new FakeVideo(), videoFields);
  const graph = new FakeGraph();
  const timers = new FakeTimers();
  const controller = createCensorController({
    video,
    graph,
    hits: [hit(11, 12)],
    setInterval: timers.setInterval,
    clearInterval: timers.clearInterval,
    ...options,
  });
  return { video, graph, timers, controller };
}

/** Advance the media and audio clocks together, as playback does. */
function advance({ video, graph }, seconds) {
  video.currentTime += seconds * video.playbackRate;
  graph.currentTime += seconds;
}

describe('censor controller: constants', () => {
  it('ticks every 100 ms', () => {
    assert.equal(TICK_MS, 100);
  });

  it('listens for the media events of the plan and ADR-0007', () => {
    assert.deepEqual(
      [...MEDIA_EVENTS].sort(),
      ['emptied', 'ended', 'loadedmetadata', 'pause', 'play', 'playing', 'ratechange', 'seeked', 'seeking', 'timeupdate', 'waiting'],
    );
  });
});

describe('censor controller: start', () => {
  it('applies a plan at once', () => {
    assert.equal(setup().graph.applies.length, 1);
  });

  it('schedules the hit ahead when playing', () => {
    assert.equal(setup().graph.lastPlan.events.length, 2);
  });

  it('schedules on the graph clock', () => {
    assert.equal(setup().graph.lastPlan.events[0].time, 101);
  });

  it('uses silence by default', () => {
    assert.equal(setup().graph.last[2], 'silence');
  });

  it('starts ticking when playing', () => {
    assert.equal(setup().timers.running, 1);
  });

  it('ticks at TICK_MS', () => {
    assert.deepEqual(setup().timers.delays, [100]);
  });

  it('listens for every media event', () => {
    assert.equal(setup().video.listenerCount, MEDIA_EVENTS.length);
  });

  it('reports filter mode', () => {
    assert.equal(setup().controller.mode, 'filter');
  });

  it('exposes the applied plan', () => {
    const { controller, graph } = setup();
    assert.equal(controller.plan, graph.lastPlan);
  });

  it('counts the apply', () => {
    assert.equal(setup().controller.applyCount, 1);
  });

  it('reports ticking', () => {
    assert.equal(setup().controller.ticking, true);
  });

  it('uses the given method', () => {
    assert.equal(setup({ method: 'bleep' }).graph.last[2], 'bleep');
  });

  it('uses the given offset', () => {
    assert.equal(setup({ offsetMs: 100 }).graph.lastPlan.events[0].time, 101.1);
  });

  it('uses the given horizon', () => {
    assert.equal(setup({ horizon: 0.5 }).graph.lastPlan.events.length, 0);
  });

  it('mutes at once in mute mode', () => {
    assert.deepEqual(setup({ mode: 'mute' }).graph.calls, [['mute']]);
  });

  it('opens at once in open mode', () => {
    assert.deepEqual(setup({ mode: 'open' }).graph.calls, [['open']]);
  });

  it('does not tick in mute mode', () => {
    assert.equal(setup({ mode: 'mute' }).timers.running, 0);
  });
});

describe('censor controller: only while really playing', () => {
  it('schedules nothing when paused', () => {
    assert.equal(setup({ video: { paused: true } }).graph.lastPlan.events.length, 0);
  });

  it('does not tick when paused', () => {
    assert.equal(setup({ video: { paused: true } }).timers.running, 0);
  });

  it('schedules nothing at readyState 2', () => {
    assert.equal(setup({ video: { readyState: 2 } }).graph.lastPlan.events.length, 0);
  });

  it('schedules at readyState 3', () => {
    assert.equal(setup({ video: { readyState: 3 } }).graph.lastPlan.events.length, 2);
  });

  it('schedules nothing at readyState 0', () => {
    assert.equal(setup({ video: { readyState: 0 } }).graph.lastPlan.events.length, 0);
  });

  it('schedules nothing while seeking', () => {
    assert.equal(setup({ video: { seeking: true } }).graph.lastPlan.events.length, 0);
  });

  it('schedules nothing at rate 0', () => {
    assert.equal(setup({ video: { playbackRate: 0 } }).graph.lastPlan.events.length, 0);
  });

  it('closes when paused inside a hit', () => {
    assert.equal(setup({ video: { paused: true, currentTime: 11.5 } }).graph.lastPlan.closedNow, true);
  });

  it('is open when paused outside a hit', () => {
    assert.equal(setup({ video: { paused: true } }).graph.lastPlan.closedNow, false);
  });
});

describe('censor controller: ticks', () => {
  it('applies nothing on a tick when nothing changed', () => {
    const s = setup();
    advance(s, 0.1);
    s.timers.tick();
    assert.equal(s.graph.applies.length, 1);
  });

  it('applies nothing over many unchanged ticks', () => {
    const s = setup({ hits: [hit(15, 16)] });
    for (let i = 0; i < 20; i++) {
      advance(s, 0.1);
      s.timers.tick();
    }
    assert.equal(s.graph.applies.length, 1);
  });

  it('applies nothing for a render quantum of jitter', () => {
    const s = setup();
    advance(s, 0.1);
    s.video.currentTime += 0.0027;
    s.timers.tick();
    assert.equal(s.graph.applies.length, 1);
  });

  it('applies when a hit enters the horizon', () => {
    const s = setup({ hits: [hit(12.5, 13)] });
    advance(s, 0.6);
    s.timers.tick();
    assert.equal(s.graph.lastPlan.events.length, 2);
  });

  it('applies once when the playhead enters a hit', () => {
    const s = setup();
    advance(s, 1.1);
    s.timers.tick();
    assert.equal(s.graph.lastPlan.closedNow, true);
  });

  it('then applies nothing more inside the hit', () => {
    const s = setup();
    advance(s, 1.1);
    s.timers.tick();
    advance(s, 0.1);
    s.timers.tick();
    assert.equal(s.graph.applies.length, 2);
  });

  it('applies when the playhead drifts past the tolerance', () => {
    const s = setup();
    s.video.currentTime += 0.02;
    s.timers.tick();
    assert.equal(s.graph.applies.length, 2);
  });

  it('does nothing on a timeupdate when nothing changed', () => {
    const s = setup();
    advance(s, 0.25);
    s.video.fire('timeupdate');
    assert.equal(s.graph.applies.length, 1);
  });
});

describe('censor controller: pause and play', () => {
  it('cancels on pause', () => {
    const s = setup();
    s.video.paused = true;
    s.video.fire('pause');
    assert.equal(s.graph.lastPlan.events.length, 0);
  });

  it('applies the cancelling plan on pause', () => {
    const s = setup();
    s.video.paused = true;
    s.video.fire('pause');
    assert.equal(s.graph.applies.length, 2);
  });

  it('stops ticking on pause', () => {
    const s = setup();
    s.video.paused = true;
    s.video.fire('pause');
    assert.equal(s.timers.running, 0);
  });

  it('keeps the gain closed when paused inside a hit', () => {
    const s = setup({ video: { currentTime: 11.5 } });
    s.video.paused = true;
    s.video.fire('pause');
    assert.equal(s.graph.lastPlan.closedNow, true);
  });

  it('re-plans on play', () => {
    const s = setup({ video: { paused: true } });
    s.video.paused = false;
    s.video.fire('play');
    assert.equal(s.graph.lastPlan.events.length, 2);
  });

  it('ticks again on play', () => {
    const s = setup({ video: { paused: true } });
    s.video.paused = false;
    s.video.fire('play');
    assert.equal(s.timers.running, 1);
  });

  it('waits on play for data (readyState below 3)', () => {
    const s = setup({ video: { paused: true, readyState: 2 } });
    s.video.paused = false;
    s.video.fire('play');
    assert.equal(s.graph.lastPlan.events.length, 0);
  });

  it('schedules on playing once data arrives', () => {
    const s = setup({ video: { paused: true, readyState: 2 } });
    s.video.paused = false;
    s.video.fire('play');
    s.video.readyState = 4;
    s.video.fire('playing');
    assert.equal(s.graph.lastPlan.events.length, 2);
  });

  it('never runs two ticks at once', () => {
    const s = setup();
    s.video.fire('play');
    s.video.fire('playing');
    s.video.fire('timeupdate');
    assert.equal(s.timers.created, 1);
  });

  it('cancels on ended', () => {
    const s = setup();
    s.video.paused = true;
    s.video.fire('ended');
    assert.equal(s.graph.lastPlan.events.length, 0);
  });
});

describe('censor controller: seeking', () => {
  it('cancels on seeking', () => {
    const s = setup();
    s.video.currentTime = 60;
    s.video.fire('seeking');
    assert.equal(s.graph.lastPlan.events.length, 0);
  });

  it('cancels on seeking even while seeking reads false', () => {
    const s = setup({ hits: [hit(11, 12), hit(61, 62)] });
    s.video.currentTime = 60;
    s.video.fire('seeking');
    assert.equal(s.graph.lastPlan.events.length, 0);
  });

  it('stops ticking on seeking', () => {
    const s = setup();
    s.video.fire('seeking');
    assert.equal(s.timers.running, 0);
  });

  it('closes at once when seeking into a hit', () => {
    const s = setup({ hits: [hit(59, 61)] });
    s.video.currentTime = 60;
    s.video.fire('seeking');
    assert.equal(s.graph.lastPlan.closedNow, true);
  });

  it('schedules nothing while the playhead is frozen after a seek', () => {
    const s = setup({ hits: [hit(61, 62)] });
    s.video.currentTime = 60;
    s.video.fire('seeking');
    s.video.fire('waiting');
    graphAdvanceOnly(s, 1.47);
    s.video.fire('timeupdate');
    assert.equal(s.graph.lastPlan.events.length, 0);
  });

  it('re-plans from the new position on seeked', () => {
    const s = setup({ hits: [hit(61, 62)] });
    s.video.currentTime = 60;
    s.video.fire('seeking');
    s.graph.currentTime = 101.47;
    s.video.fire('seeked');
    assert.equal(s.graph.lastPlan.events[0].time, 102.47);
  });

  it('ticks again after seeked', () => {
    const s = setup();
    s.video.fire('seeking');
    s.video.fire('seeked');
    assert.equal(s.timers.running, 1);
  });

  it('schedules nothing after seeked while data is short', () => {
    const s = setup();
    s.video.fire('seeking');
    s.video.fire('waiting');
    s.video.readyState = 1;
    s.video.fire('seeked');
    assert.equal(s.graph.lastPlan.events.length, 0);
  });

  it('re-plans on playing after a seek', () => {
    const s = setup({ hits: [hit(61, 62)] });
    s.video.currentTime = 60;
    s.video.fire('seeking');
    s.video.fire('waiting');
    s.video.readyState = 4;
    s.video.fire('playing');
    assert.equal(s.graph.lastPlan.events.length, 2);
  });

  it('applies a fresh plan after a seek within the horizon', () => {
    const s = setup();
    const before = s.graph.lastPlan;
    s.video.currentTime = 10.5;
    s.video.fire('seeking');
    s.video.fire('seeked');
    assert.notEqual(s.graph.lastPlan.key, before.key);
  });
});

/** Only the audio clock runs (a stall). */
function graphAdvanceOnly({ graph }, seconds) {
  graph.currentTime += seconds;
}

describe('censor controller: stalls', () => {
  it('cancels on waiting', () => {
    const s = setup();
    s.video.fire('waiting');
    assert.equal(s.graph.lastPlan.events.length, 0);
  });

  it('stops ticking on waiting', () => {
    const s = setup();
    s.video.fire('waiting');
    assert.equal(s.timers.running, 0);
  });

  it('ignores ticks and timeupdates while waiting', () => {
    const s = setup();
    s.video.fire('waiting');
    graphAdvanceOnly(s, 1);
    s.video.fire('timeupdate');
    assert.equal(s.graph.lastPlan.events.length, 0);
  });

  it('re-plans on playing after waiting', () => {
    const s = setup();
    s.video.fire('waiting');
    s.video.fire('playing');
    assert.equal(s.graph.lastPlan.events.length, 2);
  });

  it('re-plans on the audio clock that ran during the stall', () => {
    const s = setup();
    s.video.fire('waiting');
    graphAdvanceOnly(s, 1.5);
    s.video.fire('playing');
    assert.equal(s.graph.lastPlan.events[0].time, 102.5);
  });

  it('cancels on emptied', () => {
    const s = setup();
    s.video.fire('emptied');
    assert.equal(s.graph.lastPlan.events.length, 0);
  });

  it('schedules nothing after emptied until the new source plays', () => {
    const s = setup();
    s.video.fire('emptied');
    s.video.currentTime = 0;
    s.controller.setHits([hit(0.5, 1)]);
    s.video.fire('loadedmetadata');
    assert.equal(s.graph.lastPlan.events.length, 0);
  });

  it('schedules the new source on playing', () => {
    const s = setup();
    s.video.fire('emptied');
    s.video.currentTime = 0;
    s.controller.setHits([hit(0.5, 1)]);
    s.video.fire('playing');
    assert.equal(s.graph.lastPlan.events.length, 2);
  });
});

describe('censor controller: rate changes', () => {
  it('re-plans on ratechange', () => {
    const s = setup();
    s.video.playbackRate = 2;
    s.video.fire('ratechange');
    assert.equal(s.graph.lastPlan.events[0].time, 100.5);
  });

  it('applies a new plan on ratechange', () => {
    const s = setup();
    s.video.playbackRate = 2;
    s.video.fire('ratechange');
    assert.equal(s.graph.applies.length, 2);
  });

  it('cancels at rate 0', () => {
    const s = setup();
    s.video.playbackRate = 0;
    s.video.fire('ratechange');
    assert.equal(s.graph.lastPlan.events.length, 0);
  });

  it('applies nothing for a ratechange that changed nothing', () => {
    const s = setup();
    s.video.fire('ratechange');
    assert.equal(s.graph.applies.length, 1);
  });
});

describe('censor controller: settings', () => {
  it('re-plans with new hits', () => {
    const s = setup();
    s.controller.setHits([hit(10.5, 10.6)]);
    assert.equal(s.graph.lastPlan.events[0].time, 100.5);
  });

  it('applies nothing for the same hits again', () => {
    const s = setup();
    s.controller.setHits([hit(11, 12)]);
    assert.equal(s.graph.applies.length, 1);
  });

  it('applies nothing for new hits outside the horizon', () => {
    const s = setup();
    s.controller.setHits([hit(11, 12), hit(40, 41)]);
    assert.equal(s.graph.applies.length, 1);
  });

  it('opens when the hits are cleared', () => {
    const s = setup({ video: { currentTime: 11.5 } });
    s.controller.setHits([]);
    assert.equal(s.graph.lastPlan.closedNow, false);
  });

  it('reads null hits as none', () => {
    const s = setup();
    s.controller.setHits(null);
    assert.equal(s.graph.lastPlan.events.length, 0);
  });

  it('re-applies on a method change', () => {
    const s = setup();
    s.controller.setMethod('bleep');
    assert.deepEqual([s.graph.applies.length, s.graph.last[2]], [2, 'bleep']);
  });

  it('applies nothing for the same method', () => {
    const s = setup();
    s.controller.setMethod('silence');
    assert.equal(s.graph.applies.length, 1);
  });

  it('treats an unknown method as silence', () => {
    const s = setup({ method: 'bleep' });
    s.controller.setMethod('remove');
    assert.equal(s.graph.last[2], 'silence');
  });

  it('re-plans on an offset change', () => {
    const s = setup();
    s.controller.setOffset(-200);
    assert.equal(s.graph.lastPlan.events[0].time, 100.8);
  });

  it('applies nothing for an offset change within the tolerance', () => {
    const s = setup();
    s.controller.setOffset(5);
    assert.equal(s.graph.applies.length, 1);
  });

  it('refresh applies nothing when nothing changed', () => {
    const s = setup();
    s.controller.refresh();
    assert.equal(s.graph.applies.length, 1);
  });

  it('refresh picks up a change made without an event', () => {
    const s = setup();
    s.video.currentTime = 11.5;
    s.controller.refresh();
    assert.equal(s.graph.lastPlan.closedNow, true);
  });
});

describe('censor controller: modes', () => {
  it('mute closes the gain', () => {
    const s = setup();
    s.controller.setMode('mute');
    assert.deepEqual(s.graph.last, ['mute']);
  });

  it('mute stops the tick', () => {
    const s = setup();
    s.controller.setMode('mute');
    assert.equal(s.timers.running, 0);
  });

  it('mute is told once', () => {
    const s = setup();
    s.controller.setMode('mute');
    s.controller.setMode('mute');
    s.video.fire('timeupdate');
    assert.equal(s.graph.calls.filter((c) => c[0] === 'mute').length, 1);
  });

  it('mute ignores media events', () => {
    const s = setup();
    s.controller.setMode('mute');
    s.video.fire('playing');
    s.video.fire('seeked');
    assert.equal(s.graph.calls.length, 2);
  });

  it('mute ignores new hits', () => {
    const s = setup();
    s.controller.setMode('mute');
    s.controller.setHits([hit(10.5, 11)]);
    assert.deepEqual(s.graph.last, ['mute']);
  });

  it('mute does not start a tick on play', () => {
    const s = setup({ mode: 'mute', video: { paused: true } });
    s.video.paused = false;
    s.video.fire('play');
    assert.equal(s.timers.running, 0);
  });

  it('open opens the gain', () => {
    const s = setup();
    s.controller.setMode('open');
    assert.deepEqual(s.graph.last, ['open']);
  });

  it('open stops the tick', () => {
    const s = setup();
    s.controller.setMode('open');
    assert.equal(s.timers.running, 0);
  });

  it('open is told once', () => {
    const s = setup();
    s.controller.setMode('open');
    s.controller.setMode('open');
    assert.equal(s.graph.calls.filter((c) => c[0] === 'open').length, 1);
  });

  it('switches from mute to open', () => {
    const s = setup({ mode: 'mute' });
    s.controller.setMode('open');
    assert.deepEqual(s.graph.calls, [['mute'], ['open']]);
  });

  it('switches from open to mute', () => {
    const s = setup({ mode: 'open' });
    s.controller.setMode('mute');
    assert.deepEqual(s.graph.calls, [['open'], ['mute']]);
  });

  it('back to filter re-applies the plan', () => {
    const s = setup();
    s.controller.setMode('mute');
    s.controller.setMode('filter');
    assert.equal(s.graph.last[0], 'apply');
  });

  it('back to filter re-applies an identical plan', () => {
    const s = setup();
    s.controller.setMode('open');
    s.controller.setMode('filter');
    assert.equal(s.graph.applies.length, 2);
  });

  it('back to filter ticks again', () => {
    const s = setup();
    s.controller.setMode('mute');
    s.controller.setMode('filter');
    assert.equal(s.timers.running, 1);
  });

  it('filter again does nothing when already filtering', () => {
    const s = setup();
    s.controller.setMode('filter');
    assert.equal(s.graph.calls.length, 1);
  });

  it('treats an unknown mode as mute', () => {
    const s = setup();
    s.controller.setMode('whatever');
    assert.deepEqual([s.controller.mode, s.graph.last], ['mute', ['mute']]);
  });

  it('keeps settings changed while muted', () => {
    const s = setup();
    s.controller.setMode('mute');
    s.controller.setMethod('bleep');
    s.controller.setMode('filter');
    assert.equal(s.graph.last[2], 'bleep');
  });

  it('reports the mode', () => {
    const s = setup();
    s.controller.setMode('open');
    assert.equal(s.controller.mode, 'open');
  });

  it('reports no plan while open', () => {
    const s = setup();
    s.controller.setMode('open');
    assert.equal(s.controller.plan, null);
  });
});

describe('censor controller: stop', () => {
  it('removes every listener', () => {
    const s = setup();
    s.controller.stop();
    assert.equal(s.video.listenerCount, 0);
  });

  it('clears the tick', () => {
    const s = setup();
    s.controller.stop();
    assert.equal(s.timers.running, 0);
  });

  it('leaves the gain alone', () => {
    const s = setup();
    s.controller.stop();
    assert.equal(s.graph.calls.length, 1);
  });

  it('ignores later setters', () => {
    const s = setup();
    s.controller.stop();
    s.controller.setHits([hit(10.5, 11)]);
    s.controller.setMode('mute');
    s.controller.setMethod('bleep');
    s.controller.setOffset(100);
    s.controller.refresh();
    assert.equal(s.graph.calls.length, 1);
  });

  it('ignores a tick already queued', () => {
    const s = setup();
    const [queued] = [...s.timers.active.values()];
    s.controller.stop();
    s.video.currentTime = 11.5;
    queued();
    assert.equal(s.graph.calls.length, 1);
  });

  it('does not tick again on a later play', () => {
    const s = setup({ video: { paused: true } });
    s.controller.stop();
    s.video.paused = false;
    s.video.fire('play');
    assert.equal(s.timers.created, 0);
  });

  it('is idempotent', () => {
    const s = setup();
    s.controller.stop();
    s.controller.stop();
    assert.deepEqual([s.video.listenerCount, s.timers.cleared], [0, 1]);
  });

  it('reports not ticking', () => {
    const s = setup();
    s.controller.stop();
    assert.equal(s.controller.ticking, false);
  });
});

describe('censor controller: a whole word', () => {
  it('closes and opens once each over steady playback through a hit', () => {
    const s = setup({ hits: [hit(12.5, 13)] });
    for (let i = 0; i < 40; i++) {
      advance(s, 0.1);
      s.timers.tick();
    }
    // Start (nothing), the hit entering the horizon, the playhead entering it, leaving it.
    assert.equal(s.graph.applies.length, 4);
  });

  it('ends open after the hit', () => {
    const s = setup({ hits: [hit(12.5, 13)] });
    for (let i = 0; i < 40; i++) {
      advance(s, 0.1);
      s.timers.tick();
    }
    assert.deepEqual([s.graph.lastPlan.closedNow, s.graph.lastPlan.events.length], [false, 0]);
  });

  it('keeps the close at the same audio time on every apply before it', () => {
    const s = setup({ hits: [hit(12.5, 13)] });
    const closes = new Set();
    for (let i = 0; i < 20; i++) {
      advance(s, 0.1);
      s.timers.tick();
      const close = s.graph.lastPlan.events.find((e) => e.gain === 0);
      if (close) closes.add(Math.round(close.time * 1000));
    }
    assert.deepEqual([...closes], [102500]);
  });
});
