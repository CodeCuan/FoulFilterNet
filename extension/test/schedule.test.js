import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  HORIZON_EPSILON,
  HORIZON_SECONDS,
  MERGE_GAP_SECONDS,
  PLAN_TOLERANCE_SECONDS,
  isInsideHit,
  planSchedule,
  samePlan,
  validHits,
} from '../src/schedule.js';

const hit = (start, end, phrase = 'x') => ({ start, end, phrase });

/** A plan while playing at 1x, playhead 10 s, audio clock 100 s, unless overridden. */
function plan(overrides = {}) {
  return planSchedule({
    hits: [],
    currentTime: 10,
    playbackRate: 1,
    audioNow: 100,
    playing: true,
    ...overrides,
  });
}

/** The events as [time, gain] pairs, times rounded to the microsecond. */
const events = (p) => p.events.map((e) => [Math.round(e.time * 1e6) / 1e6, e.gain]);

describe('schedule: constants', () => {
  it('schedules two seconds ahead by default', () => {
    assert.equal(HORIZON_SECONDS, 2);
  });

  it('merges spans closer than 20 ms', () => {
    assert.equal(MERGE_GAP_SECONDS, 0.02);
  });

  it('treats plans within 10 ms as the same', () => {
    assert.equal(PLAN_TOLERANCE_SECONDS, 0.01);
  });

  it('allows a nanosecond past the horizon', () => {
    assert.equal(HORIZON_EPSILON, 1e-9);
  });
});

describe('schedule: validHits', () => {
  it('keeps valid hits as start/end pairs', () => {
    assert.deepEqual(validHits([hit(1, 2)]), [{ start: 1, end: 2 }]);
  });

  it('keeps the given order', () => {
    assert.deepEqual(validHits([hit(5, 6), hit(1, 2)]), [
      { start: 5, end: 6 },
      { start: 1, end: 2 },
    ]);
  });

  it('reads a non-array as no hits', () => {
    assert.deepEqual(validHits(null), []);
  });

  it('reads an object as no hits', () => {
    assert.deepEqual(validHits({ start: 1, end: 2 }), []);
  });

  it('drops null entries', () => {
    assert.deepEqual(validHits([null]), []);
  });

  it('drops numbers', () => {
    assert.deepEqual(validHits([3]), []);
  });

  it('drops a NaN start', () => {
    assert.deepEqual(validHits([hit(NaN, 2)]), []);
  });

  it('drops a NaN end', () => {
    assert.deepEqual(validHits([hit(1, NaN)]), []);
  });

  it('drops an infinite end', () => {
    assert.deepEqual(validHits([hit(1, Infinity)]), []);
  });

  it('drops string times', () => {
    assert.deepEqual(validHits([hit('1', '2')]), []);
  });

  it('drops an empty hit', () => {
    assert.deepEqual(validHits([hit(2, 2)]), []);
  });

  it('drops a reversed hit', () => {
    assert.deepEqual(validHits([hit(3, 2)]), []);
  });

  it('keeps a hit starting before zero', () => {
    assert.deepEqual(validHits([hit(-0.1, 0.2)]), [{ start: -0.1, end: 0.2 }]);
  });

  it('does not mutate its input', () => {
    const hits = [hit(1, 2)];
    validHits(hits);
    assert.deepEqual(hits, [hit(1, 2)]);
  });
});

describe('schedule: isInsideHit', () => {
  const hits = [hit(5, 6)];

  it('is inside in the middle', () => {
    assert.equal(isInsideHit(hits, 5.5), true);
  });

  it('is inside exactly at the start', () => {
    assert.equal(isInsideHit(hits, 5), true);
  });

  it('is outside exactly at the end', () => {
    assert.equal(isInsideHit(hits, 6), false);
  });

  it('is outside just before the start', () => {
    assert.equal(isInsideHit(hits, 4.999), false);
  });

  it('is outside with no hits', () => {
    assert.equal(isInsideHit([], 5.5), false);
  });

  it('is outside for a NaN time', () => {
    assert.equal(isInsideHit(hits, NaN), false);
  });

  it('is outside for an undefined time', () => {
    assert.equal(isInsideHit(hits, undefined), false);
  });

  it('ignores invalid hits', () => {
    assert.equal(isInsideHit([hit(6, 5)], 5.5), false);
  });

  it('finds the second of unsorted hits', () => {
    assert.equal(isInsideHit([hit(20, 21), hit(5, 6)], 5.5), true);
  });

  it('is outside for a non-array', () => {
    assert.equal(isInsideHit(undefined, 5.5), false);
  });
});

describe('schedule: plan shape', () => {
  it('is frozen', () => {
    assert.equal(Object.isFrozen(plan()), true);
  });

  it('has frozen events', () => {
    assert.equal(Object.isFrozen(plan({ hits: [hit(11, 12)] }).events), true);
  });

  it('has frozen event objects', () => {
    assert.equal(Object.isFrozen(plan({ hits: [hit(11, 12)] }).events[0]), true);
  });

  it('carries the audio time it was made at', () => {
    assert.equal(plan().audioNow, 100);
  });

  it('is open with nothing to do when there are no hits', () => {
    const p = plan();
    assert.deepEqual([p.closedNow, p.events.length], [false, 0]);
  });

  it('never throws on an undefined input', () => {
    assert.equal(planSchedule(undefined).closedNow, false);
  });

  it('uses audio time 0 for an undefined input', () => {
    assert.equal(planSchedule(undefined).audioNow, 0);
  });
});

describe('schedule: the horizon', () => {
  it('schedules a hit starting inside it', () => {
    assert.deepEqual(events(plan({ hits: [hit(11, 11.5)] })), [
      [101, 0],
      [101.5, 1],
    ]);
  });

  it('ignores a hit starting after it', () => {
    assert.deepEqual(events(plan({ hits: [hit(12.5, 13)] })), []);
  });

  it('ignores a hit that ended before now', () => {
    assert.deepEqual(events(plan({ hits: [hit(8, 9)] })), []);
  });

  it('includes a hit starting exactly on it', () => {
    assert.deepEqual(events(plan({ hits: [hit(12, 12.5)] })), [
      [102, 0],
      [102.5, 1],
    ]);
  });

  it('excludes a hit starting a millisecond after it', () => {
    assert.deepEqual(events(plan({ hits: [hit(12.001, 12.5)] })), []);
  });

  it('includes the boundary with float noise in the times', () => {
    // 0.1 + 2 is not exactly 2.1 in binary.
    const p = plan({ currentTime: 0.1, hits: [hit(2.1, 2.5)] });
    assert.equal(p.events.length, 2);
  });

  it('schedules the open of a straddling hit beyond it', () => {
    assert.deepEqual(events(plan({ hits: [hit(11.5, 15)] })), [
      [101.5, 0],
      [105, 1],
    ]);
  });

  it('schedules every hit inside it', () => {
    assert.equal(plan({ hits: [hit(10.5, 10.8), hit(11.2, 11.4), hit(11.8, 11.9)] }).events.length, 6);
  });

  it('takes only the hits inside it from many', () => {
    assert.deepEqual(events(plan({ hits: [hit(10.5, 10.8), hit(13, 14), hit(30, 31)] })), [
      [100.5, 0],
      [100.8, 1],
    ]);
  });

  it('honours a longer horizon', () => {
    assert.equal(plan({ horizon: 5, hits: [hit(14, 14.5)] }).events.length, 2);
  });

  it('honours a shorter horizon', () => {
    assert.equal(plan({ horizon: 0.5, hits: [hit(11, 11.5)] }).events.length, 0);
  });

  it('uses the default for a zero horizon', () => {
    assert.equal(plan({ horizon: 0, hits: [hit(11.9, 12.5)] }).events.length, 2);
  });

  it('uses the default for a negative horizon', () => {
    assert.equal(plan({ horizon: -1, hits: [hit(11.9, 12.5)] }).events.length, 2);
  });

  it('uses the default for a NaN horizon', () => {
    assert.equal(plan({ horizon: NaN, hits: [hit(11.9, 12.5)] }).events.length, 2);
  });

  it('uses the default for an infinite horizon', () => {
    assert.equal(plan({ horizon: Infinity, hits: [hit(30, 31)] }).events.length, 0);
  });
});

describe('schedule: playback rate', () => {
  it('halves the distance at 2x', () => {
    assert.deepEqual(events(plan({ playbackRate: 2, hits: [hit(11, 12)] })), [
      [100.5, 0],
      [101, 1],
    ]);
  });

  it('doubles the distance at 0.5x', () => {
    assert.deepEqual(events(plan({ playbackRate: 0.5, hits: [hit(10.5, 10.75)] })), [
      [101, 0],
      [101.5, 1],
    ]);
  });

  it('keeps the distance at 1x', () => {
    assert.deepEqual(events(plan({ playbackRate: 1, hits: [hit(11, 12)] })), [
      [101, 0],
      [102, 1],
    ]);
  });

  it('reaches four media seconds ahead at 2x', () => {
    assert.equal(plan({ playbackRate: 2, hits: [hit(14, 14.5)] }).events.length, 2);
  });

  it('does not reach past four media seconds at 2x', () => {
    assert.equal(plan({ playbackRate: 2, hits: [hit(14.01, 14.5)] }).events.length, 0);
  });

  it('reaches only one media second ahead at 0.5x', () => {
    assert.equal(plan({ playbackRate: 0.5, hits: [hit(11.01, 11.5)] }).events.length, 0);
  });

  it('includes the boundary at 0.5x', () => {
    assert.equal(plan({ playbackRate: 0.5, hits: [hit(11, 11.5)] }).events.length, 2);
  });

  it('scales an odd rate', () => {
    assert.deepEqual(events(plan({ playbackRate: 1.25, hits: [hit(11.25, 12.5)] })), [
      [101, 0],
      [102, 1],
    ]);
  });

  it('schedules nothing at rate 0', () => {
    assert.equal(plan({ playbackRate: 0, hits: [hit(11, 12)] }).events.length, 0);
  });

  it('schedules nothing at a negative rate', () => {
    assert.equal(plan({ playbackRate: -1, hits: [hit(11, 12)] }).events.length, 0);
  });

  it('schedules nothing at a NaN rate', () => {
    assert.equal(plan({ playbackRate: NaN, hits: [hit(11, 12)] }).events.length, 0);
  });

  it('schedules nothing at an infinite rate', () => {
    assert.equal(plan({ playbackRate: Infinity, hits: [hit(11, 12)] }).events.length, 0);
  });

  it('treats rate 0 inside a hit as paused inside it', () => {
    assert.equal(plan({ playbackRate: 0, hits: [hit(9, 11)] }).closedNow, true);
  });

  it('scales the open of a hit the playhead is inside', () => {
    assert.deepEqual(events(plan({ playbackRate: 2, hits: [hit(9, 11)] })), [[100.5, 1]]);
  });
});

describe('schedule: offset', () => {
  it('moves both edges later by a positive offset', () => {
    assert.deepEqual(events(plan({ offsetMs: 100, hits: [hit(11, 12)] })), [
      [101.1, 0],
      [102.1, 1],
    ]);
  });

  it('moves both edges earlier by a negative offset', () => {
    assert.deepEqual(events(plan({ offsetMs: -100, hits: [hit(11, 12)] })), [
      [100.9, 0],
      [101.9, 1],
    ]);
  });

  it('is not scaled by the rate', () => {
    assert.deepEqual(events(plan({ playbackRate: 2, offsetMs: 100, hits: [hit(11, 12)] })), [
      [100.6, 0],
      [101.1, 1],
    ]);
  });

  it('is ignored when NaN', () => {
    assert.deepEqual(events(plan({ offsetMs: NaN, hits: [hit(11, 12)] })), [
      [101, 0],
      [102, 1],
    ]);
  });

  it('is ignored when not a number', () => {
    assert.deepEqual(events(plan({ offsetMs: '100', hits: [hit(11, 12)] })), [
      [101, 0],
      [102, 1],
    ]);
  });

  it('closes now when a negative offset pulls a close into the past', () => {
    assert.equal(plan({ offsetMs: -200, hits: [hit(10.1, 11)] }).closedNow, true);
  });

  it('keeps the open after a negative offset pulls the close into the past', () => {
    assert.deepEqual(events(plan({ offsetMs: -200, hits: [hit(10.1, 11)] })), [[100.8, 1]]);
  });

  it('keeps the gain open when a positive offset delays a hit the playhead entered', () => {
    const p = plan({ offsetMs: 200, hits: [hit(9.9, 11)] });
    assert.deepEqual([p.closedNow, events(p)], [
      false,
      [
        [100.1, 0],
        [101.2, 1],
      ],
    ]);
  });

  it('drops a hit a negative offset has moved wholly into the past', () => {
    assert.equal(plan({ offsetMs: -500, hits: [hit(10.1, 10.4)] }).events.length, 0);
  });

  it('brings a hit into the horizon with a negative offset', () => {
    assert.equal(plan({ offsetMs: -100, hits: [hit(12.05, 12.5)] }).events.length, 2);
  });

  it('pushes a hit out of the horizon with a positive offset', () => {
    assert.equal(plan({ offsetMs: 100, hits: [hit(11.95, 12.5)] }).events.length, 0);
  });
});

describe('schedule: inside a hit', () => {
  it('closes now', () => {
    assert.equal(plan({ hits: [hit(9, 11)] }).closedNow, true);
  });

  it('schedules only the open', () => {
    assert.deepEqual(events(plan({ hits: [hit(9, 11)] })), [[101, 1]]);
  });

  it('closes now exactly at the start', () => {
    assert.equal(plan({ hits: [hit(10, 11)] }).closedNow, true);
  });

  it('is open exactly at the end', () => {
    assert.equal(plan({ hits: [hit(9, 10)] }).closedNow, false);
  });

  it('schedules nothing exactly at the end', () => {
    assert.equal(plan({ hits: [hit(9, 10)] }).events.length, 0);
  });

  it('schedules the open even beyond the horizon', () => {
    assert.deepEqual(events(plan({ hits: [hit(9, 20)] })), [[110, 1]]);
  });

  it('then schedules the next hit in the horizon', () => {
    assert.deepEqual(events(plan({ hits: [hit(9, 10.5), hit(11, 11.5)] })), [
      [100.5, 1],
      [101, 0],
      [101.5, 1],
    ]);
  });

  it('is open just before a hit', () => {
    assert.equal(plan({ hits: [hit(10.001, 11)] }).closedNow, false);
  });

  it('never schedules an event at or before now', () => {
    const p = plan({ hits: [hit(9, 10.3), hit(10.0001, 10.2), hit(10.5, 11)] });
    assert.equal(
      p.events.every((e) => e.time > p.audioNow),
      true,
    );
  });
});

describe('schedule: not playing', () => {
  it('schedules nothing when paused', () => {
    assert.equal(plan({ playing: false, hits: [hit(11, 12)] }).events.length, 0);
  });

  it('is open when paused outside a hit', () => {
    assert.equal(plan({ playing: false, hits: [hit(11, 12)] }).closedNow, false);
  });

  it('is closed when paused inside a hit', () => {
    assert.equal(plan({ playing: false, hits: [hit(9, 11)] }).closedNow, true);
  });

  it('schedules no open when paused inside a hit', () => {
    assert.equal(plan({ playing: false, hits: [hit(9, 11)] }).events.length, 0);
  });

  it('ignores the offset when paused', () => {
    assert.equal(plan({ playing: false, offsetMs: 500, hits: [hit(9.9, 11)] }).closedNow, true);
  });

  it('treats a missing playing flag as not playing', () => {
    assert.equal(plan({ playing: undefined, hits: [hit(11, 12)] }).events.length, 0);
  });

  it('treats a truthy non-boolean playing flag as not playing', () => {
    assert.equal(plan({ playing: 1, hits: [hit(11, 12)] }).events.length, 0);
  });

  it('schedules nothing for a NaN playhead', () => {
    assert.equal(plan({ currentTime: NaN, hits: [hit(11, 12)] }).events.length, 0);
  });

  it('is open for a NaN playhead', () => {
    assert.equal(plan({ currentTime: NaN, hits: [hit(-1, 1e9)] }).closedNow, false);
  });

  it('schedules nothing for a NaN audio clock', () => {
    assert.equal(plan({ audioNow: NaN, hits: [hit(11, 12)] }).events.length, 0);
  });

  it('reports audio time 0 for a NaN audio clock', () => {
    assert.equal(plan({ audioNow: NaN }).audioNow, 0);
  });

  it('is still closed inside a hit with a NaN audio clock', () => {
    assert.equal(plan({ audioNow: NaN, hits: [hit(9, 11)] }).closedNow, true);
  });
});

describe('schedule: merging', () => {
  it('merges overlapping hits into one span', () => {
    assert.deepEqual(events(plan({ hits: [hit(11, 11.6), hit(11.4, 12)] })), [
      [101, 0],
      [102, 1],
    ]);
  });

  it('merges touching hits', () => {
    assert.deepEqual(events(plan({ hits: [hit(11, 11.5), hit(11.5, 12)] })), [
      [101, 0],
      [102, 1],
    ]);
  });

  it('merges hits less than 20 ms apart', () => {
    assert.deepEqual(events(plan({ hits: [hit(11, 11.5), hit(11.515, 12)] })), [
      [101, 0],
      [102, 1],
    ]);
  });

  it('keeps hits more than 20 ms apart', () => {
    assert.equal(plan({ hits: [hit(11, 11.5), hit(11.53, 12)] }).events.length, 4);
  });

  it('measures the gap on the audio clock (merged at 2x)', () => {
    // 30 ms of media is 15 ms of audio at 2x.
    assert.equal(plan({ playbackRate: 2, hits: [hit(11, 11.5), hit(11.53, 12)] }).events.length, 2);
  });

  it('measures the gap on the audio clock (kept at 0.5x)', () => {
    // 15 ms of media is 30 ms of audio at 0.5x.
    assert.equal(plan({ playbackRate: 0.5, hits: [hit(10.2, 10.4), hit(10.415, 10.6)] }).events.length, 4);
  });

  it('merges a hit contained in another', () => {
    assert.deepEqual(events(plan({ hits: [hit(11, 12), hit(11.2, 11.3)] })), [
      [101, 0],
      [102, 1],
    ]);
  });

  it('merges duplicate hits', () => {
    assert.deepEqual(events(plan({ hits: [hit(11, 12), hit(11, 12)] })), [
      [101, 0],
      [102, 1],
    ]);
  });

  it('merges a chain reaching past the horizon', () => {
    assert.deepEqual(events(plan({ hits: [hit(11, 12.5), hit(12.5, 14), hit(14, 15)] })), [
      [101, 0],
      [105, 1],
    ]);
  });

  it('merges a hit the playhead is inside with the next one', () => {
    const p = plan({ hits: [hit(9, 10.5), hit(10.5, 11)] });
    assert.deepEqual([p.closedNow, events(p)], [true, [[101, 1]]]);
  });

  it('sorts unsorted hits', () => {
    assert.deepEqual(events(plan({ hits: [hit(11.5, 11.8), hit(10.5, 10.8)] })), [
      [100.5, 0],
      [100.8, 1],
      [101.5, 0],
      [101.8, 1],
    ]);
  });

  it('merges unsorted overlapping hits', () => {
    assert.deepEqual(events(plan({ hits: [hit(11.4, 12), hit(11, 11.6)] })), [
      [101, 0],
      [102, 1],
    ]);
  });

  it('keeps events alternating', () => {
    const p = plan({ hits: [hit(10.2, 10.4), hit(10.3, 10.9), hit(11.1, 11.2), hit(11.5, 11.7), hit(9, 10.1)] });
    const gains = p.events.map((e) => e.gain);
    assert.deepEqual(gains, [1, 0, 1, 0, 1, 0, 1]);
  });

  it('keeps events ascending', () => {
    const p = plan({ hits: [hit(11.5, 11.7), hit(10.2, 10.4), hit(11.1, 11.2)] });
    const times = p.events.map((e) => e.time);
    assert.deepEqual(
      times,
      [...times].sort((a, b) => a - b),
    );
  });
});

describe('schedule: invalid hits', () => {
  it('ignores a NaN hit among valid ones', () => {
    assert.deepEqual(events(plan({ hits: [hit(NaN, 11), hit(11, 12)] })), [
      [101, 0],
      [102, 1],
    ]);
  });

  it('ignores a reversed hit', () => {
    assert.equal(plan({ hits: [hit(12, 11)] }).events.length, 0);
  });

  it('ignores an empty hit', () => {
    assert.equal(plan({ hits: [hit(11, 11)] }).events.length, 0);
  });

  it('ignores a null hit', () => {
    assert.equal(plan({ hits: [null, hit(11, 12)] }).events.length, 2);
  });

  it('reads null hits as none', () => {
    assert.equal(plan({ hits: null }).events.length, 0);
  });

  it('reads a missing hits field as none', () => {
    assert.equal(planSchedule({ currentTime: 10, playbackRate: 1, audioNow: 100, playing: true }).events.length, 0);
  });

  it('does not let an invalid hit close the gain', () => {
    assert.equal(plan({ hits: [hit(11, 9)] }).closedNow, false);
  });
});

describe('schedule: near the end of the video', () => {
  it('closes now for a padded hit running past the end', () => {
    assert.equal(plan({ currentTime: 99.9, hits: [hit(99.8, 100.25)] }).closedNow, true);
  });

  it('schedules its open where the padding ends', () => {
    assert.deepEqual(events(plan({ currentTime: 99.9, hits: [hit(99.8, 100.25)] })), [[100.35, 1]]);
  });

  it('schedules a last hit starting in the final second', () => {
    assert.equal(plan({ currentTime: 99, hits: [hit(99.5, 100.25)] }).events.length, 2);
  });

  it('closes when paused at the end inside a hit', () => {
    assert.equal(plan({ playing: false, currentTime: 100, hits: [hit(99.8, 100.25)] }).closedNow, true);
  });
});

describe('schedule: plan key', () => {
  const input = { hits: [hit(11, 12)], currentTime: 10, playbackRate: 1, audioNow: 100, playing: true };

  it('is the same for identical inputs', () => {
    assert.equal(planSchedule(input).key, planSchedule({ ...input }).key);
  });

  it('is the same when both clocks advance together', () => {
    assert.equal(planSchedule(input).key, planSchedule({ ...input, currentTime: 10.1, audioNow: 100.1 }).key);
  });

  it('changes after a seek', () => {
    assert.notEqual(planSchedule(input).key, planSchedule({ ...input, currentTime: 10.5 }).key);
  });

  it('changes after a rate change', () => {
    assert.notEqual(planSchedule(input).key, planSchedule({ ...input, playbackRate: 2 }).key);
  });

  it('changes when paused', () => {
    assert.notEqual(planSchedule(input).key, planSchedule({ ...input, playing: false }).key);
  });

  it('changes with the offset', () => {
    assert.notEqual(planSchedule(input).key, planSchedule({ ...input, offsetMs: 50 }).key);
  });

  it('says whether the gain is closed now', () => {
    assert.match(planSchedule({ ...input, currentTime: 11.5 }).key, /^closed/);
  });

  it('says whether the gain is open now', () => {
    assert.match(planSchedule(input).key, /^open/);
  });

  it('is plain "open" with nothing to do', () => {
    assert.equal(planSchedule({ ...input, hits: [] }).key, 'open');
  });

  it('lists the events to the millisecond', () => {
    assert.equal(planSchedule(input).key, 'open c101.000 o102.000');
  });
});

describe('schedule: samePlan', () => {
  const input = { hits: [hit(11, 12)], currentTime: 10, playbackRate: 1, audioNow: 100, playing: true };

  it('is true for identical inputs', () => {
    assert.equal(samePlan(planSchedule(input), planSchedule(input)), true);
  });

  it('is true when both clocks advance together', () => {
    assert.equal(samePlan(planSchedule(input), planSchedule({ ...input, currentTime: 10.1, audioNow: 100.1 })), true);
  });

  it('is true for a render quantum of jitter', () => {
    assert.equal(samePlan(planSchedule(input), planSchedule({ ...input, currentTime: 10.0027 })), true);
  });

  it('is true at exactly the tolerance', () => {
    const a = planSchedule(input);
    const b = planSchedule({ ...input, audioNow: 100.0078125 });
    assert.equal(samePlan(a, b, 0.0078125), true);
  });

  it('is false just past the tolerance', () => {
    assert.equal(samePlan(planSchedule(input), planSchedule({ ...input, currentTime: 10.011 })), false);
  });

  it('is false after a seek', () => {
    assert.equal(samePlan(planSchedule(input), planSchedule({ ...input, currentTime: 10.5 })), false);
  });

  it('is false after a rate change', () => {
    assert.equal(samePlan(planSchedule(input), planSchedule({ ...input, playbackRate: 1.5 })), false);
  });

  it('is false once the playhead enters the hit', () => {
    const before = planSchedule(input);
    const inside = planSchedule({ ...input, currentTime: 11.1, audioNow: 101.1 });
    assert.equal(samePlan(before, inside), false);
  });

  it('is false when a new hit enters the horizon', () => {
    const hits = [hit(11, 12), hit(12.2, 12.5)];
    const before = planSchedule({ ...input, hits });
    const later = planSchedule({ ...input, hits, currentTime: 10.3, audioNow: 100.3 });
    assert.equal(samePlan(before, later), false);
  });

  it('is false when paused', () => {
    assert.equal(samePlan(planSchedule(input), planSchedule({ ...input, playing: false })), false);
  });

  it('is true for two empty open plans at different times', () => {
    assert.equal(samePlan(planSchedule({ ...input, hits: [] }), planSchedule({ ...input, hits: [], audioNow: 500 })), true);
  });

  it('is false for open and closed empty plans', () => {
    const open = planSchedule({ ...input, playing: false });
    const closed = planSchedule({ ...input, playing: false, currentTime: 11.5 });
    assert.equal(samePlan(open, closed), false);
  });

  it('is false against null', () => {
    assert.equal(samePlan(planSchedule(input), null), false);
  });

  it('is false for null against a plan', () => {
    assert.equal(samePlan(null, planSchedule(input)), false);
  });

  it('is false for two nulls', () => {
    assert.equal(samePlan(null, null), false);
  });
});

describe('schedule: re-planning each tick', () => {
  it('produces the same plan over 20 ticks of steady playback', () => {
    const hits = [hit(13, 14)];
    const first = planSchedule({ hits, currentTime: 10, playbackRate: 1, audioNow: 100, playing: true });
    let same = 0;
    for (let i = 1; i <= 20; i++) {
      const next = planSchedule({ hits, currentTime: 10 + i * 0.05, playbackRate: 1, audioNow: 100 + i * 0.05, playing: true });
      if (samePlan(first, next)) same++;
    }
    // The hit enters the horizon at 11 s, i.e. from tick 20.
    assert.equal(same, 19);
  });

  it('puts the events at the same audio times from any tick', () => {
    const hits = [hit(11, 12)];
    const a = planSchedule({ hits, currentTime: 10, playbackRate: 2, audioNow: 100, playing: true });
    const b = planSchedule({ hits, currentTime: 10.2, playbackRate: 2, audioNow: 100.1, playing: true });
    assert.deepEqual(events(a), events(b));
  });
});
