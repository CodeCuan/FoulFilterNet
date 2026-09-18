// gate-controller.js against a fake <video>: an EventTarget with `paused`,
// and play()/pause() that change it and queue their events, which the test
// delivers with flush() - as a browser fires them in a later task. The user's
// own presses are userPlay()/userPause(), which do the same.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { createGateController, SELF_EVENT_MS } from '../src/gate-controller.js';

class FakeVideo extends EventTarget {
  paused = true;
  playCalls = 0;
  pauseCalls = 0;
  /** @type {unknown} set to make play() reject with it */
  refusal = null;
  throwOnPlay = null;
  #queued = [];

  play() {
    this.playCalls++;
    if (this.throwOnPlay) throw this.throwOnPlay;
    if (this.refusal) return Promise.reject(this.refusal);
    this.#start();
    return Promise.resolve();
  }

  pause() {
    this.pauseCalls++;
    this.#stop();
  }

  userPlay() {
    this.#start();
  }

  userPause() {
    this.#stop();
  }

  #start() {
    if (!this.paused) return;
    this.paused = false;
    this.#queued.push('play');
  }

  #stop() {
    if (this.paused) return;
    this.paused = true;
    this.#queued.push('pause');
  }

  /** Deliver the queued events. */
  flush() {
    const events = this.#queued.splice(0);
    for (const type of events) this.dispatchEvent(new Event(type));
  }

  listenerCount = 0;
  addEventListener(type, listener) {
    this.listenerCount++;
    super.addEventListener(type, listener);
  }
  removeEventListener(type, listener) {
    this.listenerCount--;
    super.removeEventListener(type, listener);
  }
}

const HOLD = Object.freeze({ action: 'hold', reason: 'preparing', heldByUs: true, resume: false });
const RELEASE = Object.freeze({ action: 'release', reason: 'covered', heldByUs: false, resume: true });
const RELEASE_NO_RESUME = Object.freeze({ action: 'release', reason: 'covered', heldByUs: false, resume: false });
const NONE = Object.freeze({ action: 'none', reason: 'ad', heldByUs: false, resume: false });

/** A controller over a video that is playing (unless `paused`), with a hand-moved clock. */
function setup({ paused = false } = {}) {
  const video = new FakeVideo();
  video.paused = paused;
  const clock = { t: 1000 };
  const seen = { playWhileHeld: 0, userPause: 0, playErrors: [] };
  const controller = createGateController({
    video,
    now: () => clock.t,
    onPlayWhileHeld: () => seen.playWhileHeld++,
    onUserPause: () => seen.userPause++,
    onPlayError: (e) => seen.playErrors.push(e),
  });
  return { video, clock, seen, controller };
}

const tick = () => new Promise((resolve) => setImmediate(resolve));

describe('gate controller: initial state', () => {
  it('does not hold', () => {
    assert.equal(setup().controller.heldByUs, false);
  });

  it('reports no user pause', () => {
    assert.equal(setup().controller.userPaused, false);
  });

  it('reports no play request', () => {
    assert.equal(setup().controller.playRequested, false);
  });

  it('listens for play and pause', () => {
    assert.equal(setup().video.listenerCount, 2);
  });

  it('does not touch the video on creation', () => {
    const { video } = setup();
    assert.deepEqual([video.playCalls, video.pauseCalls], [0, 0]);
  });
});

describe('gate controller: hold', () => {
  it('pauses a playing video', () => {
    const { video, controller } = setup();
    controller.apply(HOLD);
    assert.equal(video.pauseCalls, 1);
  });

  it('returns paused', () => {
    assert.equal(setup().controller.apply(HOLD), 'paused');
  });

  it('holds afterwards', () => {
    const { controller } = setup();
    controller.apply(HOLD);
    assert.equal(controller.heldByUs, true);
  });

  it('does not call pause() again on a repeated hold', () => {
    const { video, controller } = setup();
    controller.apply(HOLD);
    video.flush();
    controller.apply(HOLD);
    controller.apply(HOLD);
    assert.equal(video.pauseCalls, 1);
  });

  it('returns nothing on a repeated hold', () => {
    const { video, controller } = setup();
    controller.apply(HOLD);
    video.flush();
    assert.equal(controller.apply(HOLD), 'nothing');
  });

  it('does not call pause() on a video already paused', () => {
    const { video, controller } = setup({ paused: true });
    controller.apply(HOLD);
    assert.equal(video.pauseCalls, 0);
  });

  it('returns held for a video already paused', () => {
    assert.equal(setup({ paused: true }).controller.apply(HOLD), 'held');
  });

  it('holds a video already paused', () => {
    const { controller } = setup({ paused: true });
    controller.apply(HOLD);
    assert.equal(controller.heldByUs, true);
  });

  it('counts a video already paused as the user\'s pause', () => {
    const { controller } = setup({ paused: true });
    controller.apply(HOLD);
    assert.equal(controller.userPaused, true);
  });

  it('does not count its own pause as the user\'s', () => {
    const { video, controller } = setup();
    controller.apply(HOLD);
    video.flush();
    assert.equal(controller.userPaused, false);
  });

  it('does not report its own pause event', () => {
    const { video, controller, seen } = setup();
    controller.apply(HOLD);
    video.flush();
    assert.equal(seen.userPause, 0);
  });

  it('pauses again when the video is found playing under a repeated hold', () => {
    const { video, controller } = setup();
    controller.apply(HOLD);
    video.flush();
    video.paused = false; // started with no play event delivered yet
    assert.equal(controller.apply(HOLD), 'paused');
  });
});

describe('gate controller: release', () => {
  it('plays a video it paused', () => {
    const { video, controller } = setup();
    controller.apply(HOLD);
    video.flush();
    controller.apply(RELEASE);
    assert.equal(video.playCalls, 1);
  });

  it('returns played', () => {
    const { video, controller } = setup();
    controller.apply(HOLD);
    video.flush();
    assert.equal(controller.apply(RELEASE), 'played');
  });

  it('no longer holds after a release', () => {
    const { video, controller } = setup();
    controller.apply(HOLD);
    video.flush();
    controller.apply(RELEASE);
    assert.equal(controller.heldByUs, false);
  });

  it('does not play when it does not hold', () => {
    const { video, controller } = setup({ paused: true });
    controller.apply(RELEASE);
    assert.equal(video.playCalls, 0);
  });

  it('returns nothing for a release when not holding', () => {
    assert.equal(setup({ paused: true }).controller.apply(RELEASE), 'nothing');
  });

  it('does not play again on a repeated release', () => {
    const { video, controller } = setup();
    controller.apply(HOLD);
    video.flush();
    controller.apply(RELEASE);
    video.flush();
    controller.apply(RELEASE);
    assert.equal(video.playCalls, 1);
  });

  it('does not play a video that was already paused when the hold began', () => {
    const { video, controller } = setup({ paused: true });
    controller.apply(HOLD);
    controller.apply(RELEASE);
    assert.equal(video.playCalls, 0);
  });

  it('returns released when it drops a hold without playing', () => {
    const { controller } = setup({ paused: true });
    controller.apply(HOLD);
    assert.equal(controller.apply(RELEASE), 'released');
  });

  it('does not play when the decision says not to resume', () => {
    const { video, controller } = setup();
    controller.apply(HOLD);
    video.flush();
    controller.apply(RELEASE_NO_RESUME);
    assert.equal(video.playCalls, 0);
  });

  it('does not report its own play event as a play press', () => {
    const { video, controller, seen } = setup();
    controller.apply(HOLD);
    video.flush();
    controller.apply(RELEASE);
    controller.apply(HOLD); // held again before the play event arrives
    video.flush();
    assert.equal(seen.playWhileHeld, 0);
  });

  it('does not call play() on a video that is already playing', () => {
    const { video, controller } = setup();
    controller.apply(HOLD);
    video.flush();
    video.paused = false;
    controller.apply(RELEASE);
    assert.equal(video.playCalls, 0);
  });
});

describe('gate controller: the user pauses during a hold', () => {
  /** Held by us, then the video starts under the hold and the user pauses it. */
  function heldThenUserPaused() {
    const s = setup();
    s.controller.apply(HOLD);
    s.video.flush();
    s.clock.t += SELF_EVENT_MS + 1;
    s.video.paused = false; // playing under the hold, its play event not delivered
    s.video.userPause();
    s.video.flush();
    return s;
  }

  it('does not play on release after the user paused', () => {
    const { video, controller } = heldThenUserPaused();
    controller.apply(RELEASE);
    assert.equal(video.playCalls, 0);
  });

  it('reports userPaused once the user pauses', () => {
    const { video, controller, clock } = setup();
    controller.apply(HOLD);
    video.flush();
    clock.t += SELF_EVENT_MS + 1;
    video.paused = false; // playing under the hold, no event yet
    video.userPause();
    video.flush();
    assert.equal(controller.userPaused, true);
  });

  it('reports the user\'s pause', () => {
    const { video, controller, clock, seen } = setup();
    controller.apply(HOLD);
    video.flush();
    clock.t += SELF_EVENT_MS + 1;
    video.paused = false;
    video.userPause();
    video.flush();
    assert.equal(seen.userPause, 1);
  });

  it('keeps holding after the user pauses', () => {
    const { video, controller, clock } = setup();
    controller.apply(HOLD);
    video.flush();
    clock.t += SELF_EVENT_MS + 1;
    video.paused = false;
    video.userPause();
    video.flush();
    assert.equal(controller.heldByUs, true);
  });

  it('treats a pause event long after its own pause() as the user\'s', () => {
    const { video, controller, clock } = setup();
    video.pause = () => {
      video.pauseCalls++; // the element never fires the event
      video.paused = true;
    };
    controller.apply(HOLD);
    clock.t += SELF_EVENT_MS + 1;
    video.dispatchEvent(new Event('pause'));
    assert.equal(controller.userPaused, true);
  });

  it('treats a pause event soon after its own pause() as its own', () => {
    const { video, controller, clock } = setup();
    controller.apply(HOLD);
    clock.t += SELF_EVENT_MS;
    video.flush();
    assert.equal(controller.userPaused, false);
  });

  it('counts only one pause event per pause() call as its own', () => {
    const { video, controller, seen } = setup();
    controller.apply(HOLD);
    video.flush();
    video.dispatchEvent(new Event('pause'));
    assert.equal(seen.userPause, 1);
  });

  it('does not play on release after the user paused within a second of its own pause', () => {
    const { video, controller } = setup();
    controller.apply(HOLD);
    video.flush(); // its own pause event, consumed
    video.paused = false;
    video.userPause();
    video.flush();
    controller.apply(RELEASE);
    assert.equal(video.playCalls, 0);
  });
});

describe('gate controller: a play press during a hold', () => {
  function heldAndPressed() {
    const s = setup();
    s.controller.apply(HOLD);
    s.video.flush();
    s.clock.t += 50;
    s.video.userPlay();
    s.video.flush();
    return s;
  }

  it('pauses the video again at once', () => {
    assert.equal(heldAndPressed().video.paused, true);
  });

  it('calls pause() a second time', () => {
    assert.equal(heldAndPressed().video.pauseCalls, 2);
  });

  it('reports the play press', () => {
    assert.equal(heldAndPressed().seen.playWhileHeld, 1);
  });

  it('reports playRequested', () => {
    assert.equal(heldAndPressed().controller.playRequested, true);
  });

  it('keeps holding', () => {
    assert.equal(heldAndPressed().controller.heldByUs, true);
  });

  it('does not count its re-pause as the user\'s', () => {
    const s = heldAndPressed();
    s.video.flush();
    assert.equal(s.seen.userPause, 0);
  });

  it('plays on release after the press', () => {
    const s = heldAndPressed();
    s.video.flush();
    s.controller.apply(RELEASE);
    assert.equal(s.video.playCalls, 1);
  });

  it('plays on release when the hold began on a paused video and play was pressed', () => {
    const s = setup({ paused: true });
    s.controller.apply(HOLD);
    s.video.userPlay();
    s.video.flush();
    s.video.flush();
    s.controller.apply(RELEASE);
    assert.equal(s.video.playCalls, 1);
  });

  it('clears userPaused after a play press', () => {
    const s = setup({ paused: true });
    s.controller.apply(HOLD);
    s.video.userPlay();
    s.video.flush();
    assert.equal(s.controller.userPaused, false);
  });

  it('clears playRequested on release', () => {
    const s = heldAndPressed();
    s.controller.apply(RELEASE);
    assert.equal(s.controller.playRequested, false);
  });

  it('clears playRequested when the user then pauses', () => {
    const s = heldAndPressed();
    s.video.flush();
    s.clock.t += SELF_EVENT_MS + 1;
    s.video.paused = false;
    s.video.userPause();
    s.video.flush();
    assert.equal(s.controller.playRequested, false);
  });

  it('ignores a play press when not holding', () => {
    const s = setup({ paused: true });
    s.video.userPlay();
    s.video.flush();
    assert.equal(s.video.pauseCalls, 0);
  });

  it('does not report a play press when not holding', () => {
    const s = setup({ paused: true });
    s.video.userPlay();
    s.video.flush();
    assert.equal(s.seen.playWhileHeld, 0);
  });
});

describe('gate controller: none', () => {
  it('drops its hold', () => {
    const { video, controller } = setup();
    controller.apply(HOLD);
    video.flush();
    controller.apply(NONE);
    assert.equal(controller.heldByUs, false);
  });

  it('does not play', () => {
    const { video, controller } = setup();
    controller.apply(HOLD);
    video.flush();
    controller.apply(NONE);
    assert.equal(video.playCalls, 0);
  });

  it('returns released when it had a hold', () => {
    const { video, controller } = setup();
    controller.apply(HOLD);
    video.flush();
    assert.equal(controller.apply(NONE), 'released');
  });

  it('returns nothing without a hold', () => {
    assert.equal(setup().controller.apply(NONE), 'nothing');
  });

  it('does not pause a playing video', () => {
    const { video, controller } = setup();
    controller.apply(NONE);
    assert.equal(video.pauseCalls, 0);
  });

  it('lets a play press through after dropping the hold', () => {
    const { video, controller } = setup();
    controller.apply(HOLD);
    video.flush();
    controller.apply(NONE);
    video.userPlay();
    video.flush();
    assert.equal(video.paused, false);
  });
});

describe('gate controller: odd decisions', () => {
  it('ignores an unknown action', () => {
    assert.equal(setup().controller.apply({ action: 'explode' }), 'nothing');
  });

  it('ignores null', () => {
    assert.equal(setup().controller.apply(null), 'nothing');
  });

  it('ignores an unknown action without touching the video', () => {
    const { video, controller } = setup();
    controller.apply({ action: 'explode' });
    assert.equal(video.pauseCalls, 0);
  });
});

describe('gate controller: play() refused', () => {
  it('reports a rejected play()', async () => {
    const { video, controller, seen } = setup();
    controller.apply(HOLD);
    video.flush();
    const refusal = Object.assign(new Error('no gesture'), { name: 'NotAllowedError' });
    video.refusal = refusal;
    controller.apply(RELEASE);
    await tick();
    assert.deepEqual(seen.playErrors, [refusal]);
  });

  it('does not report an AbortError (its own pause interrupted play)', async () => {
    const { video, controller, seen } = setup();
    controller.apply(HOLD);
    video.flush();
    video.refusal = Object.assign(new Error('interrupted'), { name: 'AbortError' });
    controller.apply(RELEASE);
    await tick();
    assert.equal(seen.playErrors.length, 0);
  });

  it('reports a play() that throws', () => {
    const { video, controller, seen } = setup();
    controller.apply(HOLD);
    video.flush();
    video.throwOnPlay = new Error('boom');
    controller.apply(RELEASE);
    assert.equal(seen.playErrors.length, 1);
  });

  it('copes with a play() that returns nothing', () => {
    const { video, controller } = setup();
    controller.apply(HOLD);
    video.flush();
    video.play = () => {
      video.playCalls++;
    };
    assert.equal(controller.apply(RELEASE), 'played');
  });

  it('works without any callbacks', () => {
    const video = new FakeVideo();
    video.paused = false;
    const controller = createGateController({ video });
    controller.apply(HOLD);
    video.flush();
    video.userPlay();
    video.flush();
    assert.equal(video.paused, true);
  });
});

describe('gate controller: stop', () => {
  it('removes its listeners', () => {
    const { video, controller } = setup();
    controller.stop();
    assert.equal(video.listenerCount, 0);
  });

  it('is idempotent', () => {
    const { video, controller } = setup();
    controller.stop();
    controller.stop();
    assert.equal(video.listenerCount, 0);
  });

  it('ignores decisions afterwards', () => {
    const { video, controller } = setup();
    controller.stop();
    controller.apply(HOLD);
    assert.equal(video.pauseCalls, 0);
  });

  it('ignores play presses afterwards', () => {
    const { video, controller } = setup();
    controller.apply(HOLD);
    video.flush();
    controller.stop();
    video.userPlay();
    video.flush();
    assert.equal(video.paused, false);
  });
});

describe('gate controller: a whole first viewing', () => {
  it('holds, catches a play press, then plays once covered', () => {
    const { video, controller, clock } = setup();
    const log = [];
    log.push(controller.apply(HOLD));
    video.flush();
    for (let i = 0; i < 5; i++) {
      clock.t += 1000;
      log.push(controller.apply(HOLD));
    }
    video.userPlay();
    video.flush();
    video.flush();
    log.push(controller.apply(HOLD));
    log.push(controller.apply(RELEASE));
    video.flush();
    assert.deepEqual(
      [log, video.paused, video.pauseCalls, video.playCalls],
      [['paused', 'nothing', 'nothing', 'nothing', 'nothing', 'nothing', 'nothing', 'played'], false, 2, 1],
    );
  });
});
