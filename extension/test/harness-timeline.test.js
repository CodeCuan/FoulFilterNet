// What happened during one harness run (W16).

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';

import { createTimeline, summarise } from '../harness/timeline.js';

/** A timeline on a hand-moved clock. */
function timeline(start = 1000) {
  let now = start;
  const t = createTimeline(() => now);
  return {
    ...t,
    at(ms) {
      now = start + ms;
      return this;
    },
  };
}

describe('createTimeline', () => {
  it('stamps entries from the start of the run', () => {
    const t = timeline().at(250);
    t.note('play', 'pressed', 0);
    assert.deepEqual(t.entries, [{ at: 250, media: 0, kind: 'play', value: 'pressed' }]);
  });

  it('rounds the time to whole milliseconds', () => {
    const t = timeline().at(12.6);
    t.note('note', 'x');
    assert.equal(t.entries[0].at, 13);
  });

  it('keeps a missing media time as null', () => {
    const t = timeline();
    t.note('note', 'x');
    t.note('note', 'y', NaN);
    assert.deepEqual(
      t.entries.map((e) => e.media),
      [null, null],
    );
  });

  it('skips a gate entry equal to the last gate entry', () => {
    const t = timeline();
    t.note('gate', 'hold:starting');
    t.note('gate', 'hold:starting');
    assert.equal(t.entries.length, 1);
  });

  it('skips a repeated state and badge the same way', () => {
    const t = timeline();
    t.note('state', 'transcribing');
    t.note('badge', '…');
    t.note('state', 'transcribing');
    t.note('badge', '…');
    assert.equal(t.entries.length, 2);
  });

  it('skips a repeated coverage', () => {
    const t = timeline();
    t.note('coverage', '[[0,24]]');
    t.note('coverage', '[[0,24]]');
    t.note('coverage', '[[0,46]]');
    assert.deepEqual(
      t.entries.map((e) => e.value),
      ['[[0,24]]', '[[0,46]]'],
    );
  });

  it('keeps repeated events of other kinds', () => {
    const t = timeline();
    t.note('seek', '1');
    t.note('seek', '1');
    assert.equal(t.entries.length, 2);
  });

  it('keeps a value that comes back after a change', () => {
    const t = timeline();
    t.note('gate', 'hold:starting');
    t.note('gate', 'none:ok');
    t.note('gate', 'hold:starting');
    assert.equal(t.entries.length, 3);
  });

  it('freezes entries', () => {
    const t = timeline();
    t.note('note', 'x');
    assert.ok(Object.isFrozen(t.entries[0]));
  });
});

describe('summarise: a first view that holds, then plays', () => {
  const t = timeline();
  t.at(0).note('gate', 'hold:starting', 0);
  t.at(0).note('state', 'queued');
  t.at(500).note('play', 'pressed', 0);
  t.at(600).note('state', 'fetching');
  t.at(1500).note('state', 'transcribing');
  t.at(1600).note('gate', 'hold:preparing', 0);
  t.at(3200).note('state', 'complete');
  t.at(3300).note('gate', 'release:complete', 0);
  t.at(3400).note('gate', 'none:complete', 0.1);
  t.at(3300).note('badge', '✓');
  const s = summarise(t.entries);

  it('counts one hold', () => {
    assert.equal(s.holds, 1);
  });

  it('counts one release', () => {
    assert.equal(s.releases, 1);
  });

  it('measures the wait from the press to the release', () => {
    assert.equal(s.firstPlayMs, 2800);
  });

  it('measures the time held', () => {
    assert.equal(s.heldMs, 3300);
  });

  it('lists the session states in order', () => {
    assert.deepEqual(s.states, ['queued', 'fetching', 'transcribing', 'complete']);
  });

  it('lists the hold reasons', () => {
    assert.deepEqual(s.gateReasons, ['starting', 'preparing']);
  });

  it('lists the badges', () => {
    assert.deepEqual(s.badges, ['✓']);
  });

  it('is frozen', () => {
    assert.ok(Object.isFrozen(s));
  });
});

describe('summarise: a cached view that never holds', () => {
  const t = timeline();
  t.at(0).note('gate', 'none:not-watching');
  t.at(100).note('play', 'pressed', 0);
  t.at(130).note('playing', '', 0);
  const s = summarise(t.entries);

  it('counts no holds', () => {
    assert.equal(s.holds, 0);
  });

  it('measures the wait to the first playing', () => {
    assert.equal(s.firstPlayMs, 30);
  });

  it('has held for no time', () => {
    assert.equal(s.heldMs, 0);
  });
});

describe('summarise: a hold that starts after the press', () => {
  const t = timeline();
  t.at(0).note('play', 'pressed', 0);
  t.at(10).note('playing', '', 0);
  t.at(20).note('gate', 'hold:starting', 0);
  t.at(900).note('gate', 'release:complete', 0);

  it('waits for the release, not the brief playing', () => {
    assert.equal(summarise(t.entries).firstPlayMs, 900);
  });
});

describe('summarise: still holding at the end', () => {
  const t = timeline();
  t.at(0).note('gate', 'hold:error');
  t.at(100).note('play', 'pressed');
  t.at(4000).note('note', 'gave up');
  const s = summarise(t.entries);

  it('counts the hold', () => {
    assert.equal(s.holds, 1);
  });

  it('counts no release', () => {
    assert.equal(s.releases, 0);
  });

  it('has no wait, since it never played', () => {
    assert.equal(s.firstPlayMs, null);
  });

  it('counts the time held up to the last entry', () => {
    assert.equal(s.heldMs, 4000);
  });
});

describe('summarise: two holds (a seek past the coverage)', () => {
  const t = timeline();
  t.at(0).note('gate', 'hold:starting');
  t.at(1000).note('gate', 'release:complete');
  t.at(5000).note('gate', 'hold:preparing');
  t.at(5500).note('gate', 'release:preparing');
  const s = summarise(t.entries);

  it('counts both', () => {
    assert.equal(s.holds, 2);
    assert.equal(s.releases, 2);
  });

  it('adds up the time held', () => {
    assert.equal(s.heldMs, 1500);
  });
});

describe('summarise: odd input', () => {
  it('copes with nothing', () => {
    assert.deepEqual(summarise([]), {
      holds: 0,
      releases: 0,
      firstPlayMs: null,
      heldMs: null,
      states: [],
      badges: [],
      gateReasons: [],
    });
  });

  it('copes with a non-array', () => {
    assert.equal(summarise(undefined).holds, 0);
  });

  it('has no wait without a press', () => {
    const t = timeline();
    t.note('playing', '');
    assert.equal(summarise(t.entries).firstPlayMs, null);
  });
});
