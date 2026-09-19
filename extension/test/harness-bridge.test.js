// The harness's stand-ins for Chrome (W16): the fetch bridge that turns the
// fake YouTube video into a dev file, the fake window, the memory settings
// store and the badge recorder - and the real relay and api-client running
// through them.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';

import {
  FAKE_VIDEO_ID,
  FAKE_WATCH_URL,
  FILE_PROVIDER,
  createBadgeRecorder,
  createFileBridge,
  createMemorySettings,
  harnessWindow,
} from '../harness/file-bridge.js';
import { createRelay } from '../src/relay.js';
import { badgeMessage, cancelMessage, createMessenger, getMessage, heartbeatMessage } from '../src/protocol.js';
import { DEFAULT_SETTINGS } from '../src/settings.js';
import { isVideoId, videoIdFromUrl } from '../src/video-id.js';

const ORIGIN = 'http://localhost:8000';
const FILE = 'sample_video.mp4';

/** A view as the service sends it for the dev file. */
function fileView(overrides = {}) {
  return {
    key: `file-${FILE}`,
    provider: 'file',
    video_id: FILE,
    session: 'abc',
    state: 'complete',
    reason: null,
    failure_kind: null,
    title: FILE,
    duration: 5.649,
    revision: 1,
    unchanged: false,
    coverage: [[0, 5.649]],
    hits: [{ start: 2.79, end: 3.68, phrase: 'damn' }],
    windows_done: 1,
    windows_total: 1,
    progress: 1,
    realtime_factor: 20,
    keeping_up: true,
    from_cache: false,
    ...overrides,
  };
}

/** A fake fetch that records requests and answers with `reply(url, init)`. */
function recordingFetch(reply = () => ({ status: 200, body: JSON.stringify(fileView()) })) {
  const calls = [];
  const fetch = async (url, init = {}) => {
    calls.push({ url, init });
    const { status, body, statusText = '' } = reply(url, init);
    return new Response(body, { status, statusText, headers: { 'Content-Type': 'application/json' } });
  };
  return { fetch, calls };
}

describe('the fake video', () => {
  it('is a valid YouTube ID', () => {
    assert.equal(isVideoId(FAKE_VIDEO_ID), true);
  });

  it('is what page.js reads from the fake watch URL', () => {
    assert.equal(videoIdFromUrl(FAKE_WATCH_URL), FAKE_VIDEO_ID);
  });

  it('is served by the file provider', () => {
    assert.equal(FILE_PROVIDER, 'file');
  });
});

describe('createFileBridge: a heartbeat', () => {
  const { fetch, calls } = recordingFetch();
  const bridged = createFileBridge({ fetch, fileName: FILE });
  const body = JSON.stringify({ provider: 'youtube', video_id: FAKE_VIDEO_ID, position: 1.5 });
  const answer = bridged(`${ORIGIN}/watch?since=1&session=abc`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body,
  });

  it('posts to the same URL', async () => {
    await answer;
    assert.equal(calls[0].url, `${ORIGIN}/watch?since=1&session=abc`);
  });

  it('asks for the file instead of the fake video', async () => {
    await answer;
    assert.deepEqual(JSON.parse(calls[0].init.body), { provider: 'file', video_id: FILE, position: 1.5 });
  });

  it('keeps the method and headers', async () => {
    await answer;
    assert.equal(calls[0].init.method, 'POST');
    assert.deepEqual(calls[0].init.headers, { 'Content-Type': 'application/json' });
  });

  it('answers with the fake video', async () => {
    const view = JSON.parse(await (await answer).clone().text());
    assert.equal(view.provider, 'youtube');
    assert.equal(view.video_id, FAKE_VIDEO_ID);
    assert.equal(view.key, `youtube-${FAKE_VIDEO_ID}`);
  });

  it('keeps everything else in the view', async () => {
    const view = JSON.parse(await (await answer).clone().text());
    assert.deepEqual(view.hits, fileView().hits);
    assert.equal(view.session, 'abc');
    assert.equal(view.state, 'complete');
  });

  it('keeps the status', async () => {
    assert.equal((await answer).status, 200);
  });
});

describe('createFileBridge: a read and a cancel', () => {
  it('reads the file session for the fake video', async () => {
    const { fetch, calls } = recordingFetch();
    await createFileBridge({ fetch, fileName: FILE })(`${ORIGIN}/watch/youtube/${FAKE_VIDEO_ID}?since=2&session=s`, {
      method: 'GET',
    });
    assert.equal(calls[0].url, `${ORIGIN}/watch/file/${FILE}?since=2&session=s`);
  });

  it('cancels the file session for the fake video', async () => {
    const { fetch, calls } = recordingFetch(() => ({ status: 204, body: null }));
    const response = await createFileBridge({ fetch, fileName: FILE })(`${ORIGIN}/watch/youtube/${FAKE_VIDEO_ID}`, {
      method: 'DELETE',
    });
    assert.equal(calls[0].url, `${ORIGIN}/watch/file/${FILE}`);
    assert.equal(response.status, 204);
  });

  it('escapes the file name in the path', async () => {
    const { fetch, calls } = recordingFetch();
    await createFileBridge({ fetch, fileName: 'a b.mp4' })(`${ORIGIN}/watch/youtube/${FAKE_VIDEO_ID}`);
    assert.equal(calls[0].url, `${ORIGIN}/watch/file/a%20b.mp4`);
  });
});

describe('createFileBridge: everything else passes through', () => {
  it('leaves another video alone', async () => {
    const { fetch, calls } = recordingFetch(() => ({ status: 200, body: '{}' }));
    const body = JSON.stringify({ provider: 'youtube', video_id: 'jNQXAC9IVRw', position: 0 });
    await createFileBridge({ fetch, fileName: FILE })(`${ORIGIN}/watch`, { method: 'POST', body });
    assert.equal(calls[0].init.body, body);
  });

  it('leaves another video path alone', async () => {
    const { fetch, calls } = recordingFetch(() => ({ status: 200, body: '{}' }));
    await createFileBridge({ fetch, fileName: FILE })(`${ORIGIN}/watch/youtube/jNQXAC9IVRw`);
    assert.equal(calls[0].url, `${ORIGIN}/watch/youtube/jNQXAC9IVRw`);
  });

  it('leaves /config alone', async () => {
    const { fetch, calls } = recordingFetch(() => ({ status: 200, body: '{"web_video":{}}' }));
    const response = await createFileBridge({ fetch, fileName: FILE })(`${ORIGIN}/config`);
    assert.equal(calls[0].url, `${ORIGIN}/config`);
    assert.equal(await response.text(), '{"web_video":{}}');
  });

  it('leaves a body that is not JSON alone', async () => {
    const { fetch, calls } = recordingFetch(() => ({ status: 200, body: '{}' }));
    await createFileBridge({ fetch, fileName: FILE })(`${ORIGIN}/watch`, { method: 'POST', body: 'not json' });
    assert.equal(calls[0].init.body, 'not json');
  });

  it('leaves an answer that is not JSON alone', async () => {
    const { fetch } = recordingFetch(() => ({ status: 500, body: 'oops', statusText: 'Server Error' }));
    const response = await createFileBridge({ fetch, fileName: FILE })(`${ORIGIN}/watch`, { method: 'POST' });
    assert.equal(await response.text(), 'oops');
    assert.equal(response.status, 500);
    assert.equal(response.statusText, 'Server Error');
  });

  it('leaves a view of another file alone', async () => {
    const other = JSON.stringify(fileView({ video_id: 'other.mp4', key: 'file-other.mp4' }));
    const { fetch } = recordingFetch(() => ({ status: 200, body: other }));
    const response = await createFileBridge({ fetch, fileName: FILE })(`${ORIGIN}/watch`, { method: 'POST' });
    assert.equal(await response.text(), other);
  });

  it('passes an error detail through', async () => {
    const { fetch } = recordingFetch(() => ({ status: 400, body: '{"detail":"no"}' }));
    const response = await createFileBridge({ fetch, fileName: FILE })(`${ORIGIN}/watch`, { method: 'POST' });
    assert.equal(await response.text(), '{"detail":"no"}');
  });

  it('passes a rejected fetch on', async () => {
    const fetch = async () => {
      throw new TypeError('Failed to fetch');
    };
    await assert.rejects(createFileBridge({ fetch, fileName: FILE })(`${ORIGIN}/watch`), TypeError);
  });

  it('passes the abort signal on', async () => {
    const { fetch, calls } = recordingFetch();
    const controller = new AbortController();
    await createFileBridge({ fetch, fileName: FILE })(`${ORIGIN}/watch`, { method: 'POST', signal: controller.signal });
    assert.equal(calls[0].init.signal, controller.signal);
  });
});

describe('the real relay and api-client through the bridge', () => {
  /** A relay as the harness builds it, over a fake service. */
  function harnessRelay(reply) {
    const { fetch, calls } = recordingFetch(reply);
    const settings = createMemorySettings({ ...DEFAULT_SETTINGS, serverUrl: ORIGIN });
    const action = createBadgeRecorder();
    const relay = createRelay({ loadSettings: settings.load, fetch: createFileBridge({ fetch, fileName: FILE }), action });
    const send = createMessenger((message) => relay.handle(message, { tab: { id: 1 } }));
    return { send, calls, action };
  }

  it('gets a view of the fake video for a heartbeat', async () => {
    const { send } = harnessRelay();
    const reply = await send(heartbeatMessage({ provider: 'youtube', videoId: FAKE_VIDEO_ID, position: 0 }));
    assert.equal(reply.ok, true);
    assert.equal(reply.view.videoId, FAKE_VIDEO_ID);
    assert.equal(reply.view.provider, 'youtube');
    assert.deepEqual(reply.view.hits, [{ start: 2.79, end: 3.68, phrase: 'damn' }]);
  });

  it('posts the file to the service', async () => {
    const { send, calls } = harnessRelay();
    await send(heartbeatMessage({ provider: 'youtube', videoId: FAKE_VIDEO_ID, position: 2.5, since: 1, session: 'abc' }));
    assert.equal(calls[0].url, `${ORIGIN}/watch?since=1&session=abc`);
    assert.deepEqual(JSON.parse(calls[0].init.body), { provider: 'file', video_id: FILE, position: 2.5 });
  });

  it('reads the file session', async () => {
    const { send, calls } = harnessRelay();
    const reply = await send(getMessage({ provider: 'youtube', videoId: FAKE_VIDEO_ID }));
    assert.equal(reply.ok, true);
    assert.equal(calls[0].url, `${ORIGIN}/watch/file/${FILE}`);
  });

  it('cancels the file session', async () => {
    const { send } = harnessRelay(() => ({ status: 204, body: null }));
    const reply = await send(cancelMessage({ provider: 'youtube', videoId: FAKE_VIDEO_ID }));
    assert.deepEqual(reply, { ok: true, cancelled: true });
  });

  it('reports an unreachable service as the extension would', async () => {
    const settings = createMemorySettings({ ...DEFAULT_SETTINGS, serverUrl: ORIGIN });
    const fetch = async () => {
      throw new TypeError('Failed to fetch');
    };
    const relay = createRelay({ loadSettings: settings.load, fetch: createFileBridge({ fetch, fileName: FILE }) });
    const reply = await relay.handle(heartbeatMessage({ provider: 'youtube', videoId: FAKE_VIDEO_ID, position: 0 }));
    assert.equal(reply.ok, false);
    assert.equal(reply.error.kind, 'unreachable');
  });

  it('shows the badge on the recorder', async () => {
    const { send, action } = harnessRelay();
    const reply = await send(badgeMessage({ text: '✓', color: '#188038', title: 'FoulFilter: filtering' }));
    await Promise.resolve();
    assert.deepEqual(reply, { ok: true });
    assert.deepEqual(action.badge, { text: '✓', color: '#188038', title: 'FoulFilter: filtering' });
  });
});

describe('harnessWindow', () => {
  function fakeWindow() {
    const added = [];
    const removed = [];
    class FakeObserver {}
    return {
      added,
      removed,
      FakeObserver,
      win: {
        location: { href: 'http://localhost:8000/dev/harness/' },
        addEventListener: (...args) => added.push(args),
        removeEventListener: (...args) => removed.push(args),
        MutationObserver: FakeObserver,
      },
    };
  }

  it('claims to be the fake watch page', () => {
    assert.equal(harnessWindow(fakeWindow().win).location.href, FAKE_WATCH_URL);
  });

  it('can claim another URL', () => {
    assert.equal(harnessWindow(fakeWindow().win, 'https://www.youtube.com/').location.href, 'https://www.youtube.com/');
  });

  it('adds listeners to the real window', () => {
    const { win, added } = fakeWindow();
    const listener = () => {};
    harnessWindow(win).addEventListener('pagehide', listener);
    assert.deepEqual(added, [['pagehide', listener]]);
  });

  it('removes listeners from the real window', () => {
    const { win, removed } = fakeWindow();
    const listener = () => {};
    harnessWindow(win).removeEventListener('pageshow', listener);
    assert.deepEqual(removed, [['pageshow', listener]]);
  });

  it('hands over the real MutationObserver', () => {
    const { win, FakeObserver } = fakeWindow();
    assert.equal(harnessWindow(win).MutationObserver, FakeObserver);
  });

  it('cannot have its location changed', () => {
    const w = harnessWindow(fakeWindow().win);
    assert.throws(() => {
      'use strict';
      w.location.href = 'x';
    }, TypeError);
  });
});

describe('createMemorySettings', () => {
  it('loads what it was given', async () => {
    const store = createMemorySettings({ ...DEFAULT_SETTINGS, censorMethod: 'bleep' });
    assert.equal((await store.load()).censorMethod, 'bleep');
  });

  it('tells subscribers about an update', () => {
    const store = createMemorySettings(DEFAULT_SETTINGS);
    const seen = [];
    store.subscribe((next) => seen.push(next.censorMethod));
    store.update({ censorMethod: 'bleep' });
    assert.deepEqual(seen, ['bleep']);
  });

  it('loads the update afterwards', async () => {
    const store = createMemorySettings(DEFAULT_SETTINGS);
    store.update({ offsetMs: 40 });
    assert.equal((await store.load()).offsetMs, 40);
  });

  it('stops telling an unsubscribed listener', () => {
    const store = createMemorySettings(DEFAULT_SETTINGS);
    const seen = [];
    const unsubscribe = store.subscribe((next) => seen.push(next));
    unsubscribe();
    store.update({ enabled: false });
    assert.deepEqual(seen, []);
  });

  it('hands out frozen settings', async () => {
    assert.ok(Object.isFrozen(await createMemorySettings(DEFAULT_SETTINGS).load()));
  });

  it('does not change what it was given', () => {
    const initial = { ...DEFAULT_SETTINGS };
    createMemorySettings(initial).update({ enabled: false });
    assert.equal(initial.enabled, true);
  });
});

describe('createBadgeRecorder', () => {
  it('starts blank', () => {
    assert.deepEqual(createBadgeRecorder().badge, { text: '', color: '', title: '' });
  });

  it('reports one change for the three calls the relay makes', async () => {
    const seen = [];
    const action = createBadgeRecorder((b) => seen.push(b));
    action.setBadgeText({ tabId: 1, text: '…' });
    action.setBadgeBackgroundColor({ tabId: 1, color: '#1a73e8' });
    action.setTitle({ tabId: 1, title: 'FoulFilter: preparing' });
    await Promise.resolve();
    assert.deepEqual(seen, [{ text: '…', color: '#1a73e8', title: 'FoulFilter: preparing' }]);
  });

  it('keeps the history of badges', async () => {
    const action = createBadgeRecorder();
    action.setBadgeText({ tabId: 1, text: '…' });
    await Promise.resolve();
    action.setBadgeText({ tabId: 1, text: '✓' });
    await Promise.resolve();
    assert.deepEqual(
      action.history.map((b) => b.text),
      ['…', '✓'],
    );
  });

  it('does not repeat an unchanged badge', async () => {
    const action = createBadgeRecorder();
    action.setBadgeText({ tabId: 1, text: '✓' });
    await Promise.resolve();
    action.setBadgeText({ tabId: 1, text: '✓' });
    await Promise.resolve();
    assert.equal(action.history.length, 1);
  });
});
